' ============================================================
' LiveStrategiesController.vb
'
' Controlador singleton que orquesta la ejecucion de estrategias
' en vivo, recibiendo ordenes desde:
'   - El servidor SignalR (via CommandRouter).
'   - La UI (ventana ServidorLive con botones manuales).
'
' Responsabilidades:
'   - Mantener un diccionario sesionId -> LiveTestMotor activo.
'   - Hacer streaming de velas via BackgroundStreamingSubscriber y
'     dispatchear cada cierre a TODOS los motores que correspondan.
'   - Persistir/cargar sesiones en BD via LiveTestRepository.
'   - Reanudar al iniciar la app las sesiones live "Abierto" que
'     quedaron de un crash previo.
'   - Conectar cada motor con TradingServiceLocator.Current para
'     ejecutar trades reales en Deriv via MainWindow.
' ============================================================
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas

Namespace WpfDerivClientVB.Servidor

    Public Class LiveStrategiesController

#Region "Singleton"
        Private Shared _instance As LiveStrategiesController
        Private Shared ReadOnly _lockObj As New Object()

        Public Shared Function GetInstance() As LiveStrategiesController
            If _instance Is Nothing Then
                SyncLock _lockObj
                    If _instance Is Nothing Then
                        _instance = New LiveStrategiesController()
                    End If
                End SyncLock
            End If
            Return _instance
        End Function
#End Region

#Region "Estado interno"
        ''' <summary>Motores activos por sesionId.</summary>
        Private ReadOnly _motors As New ConcurrentDictionary(Of Integer, LiveTestMotor)()

        ''' <summary>Repository unico (BD).</summary>
        Private ReadOnly _repo As New LiveTestRepository()

        ''' <summary>Subscripcion a velas multi-symbol.</summary>
        Private _subscriber As BackgroundStreamingSubscriber

        ''' <summary>Callback opcional para log/diagnostico.</summary>
        Public Property LogAction As Action(Of String) = Nothing

        ''' <summary>
        ''' Disparado cada vez que un motor cambia de estado (op abierta, cerrada,
        ''' pausa, etc.). Permite a la UI y al CommandRouter reportar al servidor.
        ''' </summary>
        Public Event MotorActualizado(sesionId As Integer, estado As JObject)
#End Region

#Region "Init"
        Private Sub New()
            _subscriber = New BackgroundStreamingSubscriber() With {
                .OnCandleCerrada = AddressOf DispatchCandleCerrada,
                .LogAction = Sub(m) Log(m)
            }
        End Sub

        Private Sub Log(msg As String)
            Try
                If LogAction IsNot Nothing Then LogAction.Invoke(msg)
            Catch
            End Try
        End Sub

        ''' <summary>Lista de sesionIds actualmente activas.</summary>
        Public ReadOnly Property SesionesActivas As List(Of Integer)
            Get
                Return _motors.Keys.ToList()
            End Get
        End Property

        ''' <summary>Cuantos motores estan activos.</summary>
        Public ReadOnly Property Count As Integer
            Get
                Return _motors.Count
            End Get
        End Property

        ''' <summary>Diccionario directo (uso interno UI). No mutar desde fuera.</summary>
        Public ReadOnly Property Motores As ConcurrentDictionary(Of Integer, LiveTestMotor)
            Get
                Return _motors
            End Get
        End Property
#End Region

#Region "Iniciar / Detener"

        ''' <summary>
        ''' Pone una estrategia al aire. Crea el motor, registra sesion en BD,
        ''' suscribe el simbolo en streaming si aun no estaba, y conecta el motor
        ''' con TradingServiceLocator para ejecutar trades en Deriv.
        ''' Devuelve sesionId (o -1 si fallo).
        ''' </summary>
        Public Async Function IniciarAsync(estrategiaNombre As String, symbol As String, timeframe As String,
                                            confianza As Integer, payout As Decimal, duracionVelas As Integer,
                                            monto As Decimal,
                                            Optional ventanaIndicadores As Integer = 100,
                                            Optional horasOperacion As Integer = 0,
                                            Optional limiteGanancia As Decimal = 0D,
                                            Optional limitePerdida As Decimal = 0D
                                            ) As Task(Of Integer)

            ' 1) Validar estrategia
            EstrategiasDinamicasRegistry.Inicializar()
            Dim defEst = EstrategiasDinamicasRegistry.GetByNombre(estrategiaNombre)
            If defEst Is Nothing Then
                Throw New Exception("Estrategia no encontrada en el registry: " & estrategiaNombre)
            End If

            ' 2) Validar trading service disponible (sin esto no podemos ejecutar trades reales)
            If Not TradingServiceLocator.IsAvailable Then
                Log("[LiveCtrl] WARN: TradingServiceLocator no registrado. Las ops abiertas no llegaran a Deriv.")
            End If

            ' 3) Construir config
            Dim config As New LiveTestConfig With {
                .Symbol = symbol,
                .Timeframe = timeframe,
                .DuracionVelas = duracionVelas,
                .PayoutPct = payout,
                .MinConfianza = confianza,
                .MontoOperacion = monto,
                .VentanaIndicadores = ventanaIndicadores,
                .HorasOperacion = horasOperacion,
                .LimiteGanancia = limiteGanancia,
                .LimitePerdida = limitePerdida
            }

            ' 4) Instanciar motor + cablear callbacks
            Dim motor As New LiveTestMotor()
            motor.ExecuteTrade = Function(body As JObject) ApiExecuteTrade(body)

            ' 5) Iniciar sesion en BD
            Dim sesionId = Await motor.IniciarAsync(config, defEst, "live")
            If sesionId <= 0 Then
                Throw New Exception("No se pudo crear sesion en BD para " & estrategiaNombre)
            End If

            ' 6) Registrar en diccionario
            _motors(sesionId) = motor
            Log(String.Format("[LiveCtrl] Motor {0} iniciado: {1} {2}/{3}",
                              sesionId, estrategiaNombre, symbol, timeframe))

            ' 7) Suscribir al streaming (si no lo estaba ya)
            If Not _subscriber.EstaSuscrito(symbol, timeframe) Then
                Dim granularity = TimeframeAGranularidad(timeframe)
                Await _subscriber.SubscribeAsync(symbol, timeframe, granularity)
            End If

            ' 8) Notificar UI
            RaiseEvent MotorActualizado(sesionId, motor.GetEstado())

            Return sesionId
        End Function

        ''' <summary>Detiene una sesion activa, persiste metricas finales y desuscribe si nadie usa el simbolo.</summary>
        Public Async Function DetenerAsync(sesionId As Integer) As Task(Of Boolean)
            Dim motor As LiveTestMotor = Nothing
            If Not _motors.TryGetValue(sesionId, motor) Then Return False

            Dim symbol = ""
            Dim timeframe = ""
            Try
                Dim est = motor.GetEstado()
                symbol = If(est("symbol")?.ToString(), "")
                timeframe = If(est("timeframe")?.ToString(), "")
            Catch
            End Try

            Try
                Await motor.DetenerAsync()
            Catch ex As Exception
                Log("[LiveCtrl] Error al detener motor #" & sesionId & ": " & ex.Message)
            End Try

            _motors.TryRemove(sesionId, motor)
            Log("[LiveCtrl] Motor #" & sesionId & " detenido.")

            ' Si no queda otro motor sobre ese symbol/tf, desuscribir
            If symbol <> "" AndAlso timeframe <> "" Then
                Dim alguienMas = _motors.Values.Any(Function(m)
                                                        Try
                                                            Dim e = m.GetEstado()
                                                            Return e("symbol")?.ToString() = symbol AndAlso
                                                                   e("timeframe")?.ToString() = timeframe
                                                        Catch
                                                            Return False
                                                        End Try
                                                    End Function)
                If Not alguienMas Then
                    Try
                        Await _subscriber.UnsubscribeAsync(symbol, timeframe)
                    Catch
                    End Try
                End If
            End If

            RaiseEvent MotorActualizado(sesionId, New JObject From {{"sesionId", sesionId}, {"estado", "detenido"}})
            Return True
        End Function

        ''' <summary>Pausa o reanuda una estrategia activa.</summary>
        Public Function PausarReanudar(sesionId As Integer, pausar As Boolean) As Boolean
            Dim motor As LiveTestMotor = Nothing
            If Not _motors.TryGetValue(sesionId, motor) Then Return False
            If pausar Then
                motor.Pausar()
            Else
                motor.Reanudar()
            End If
            RaiseEvent MotorActualizado(sesionId, motor.GetEstado())
            Return True
        End Function

        ''' <summary>Actualiza parametros de un motor en runtime sin detenerlo.</summary>
        Public Function ActualizarConfig(sesionId As Integer, monto As Decimal, minConfianza As Integer,
                                          duracionVelas As Integer, horasOperacion As Integer,
                                          limiteGanancia As Decimal, limitePerdida As Decimal) As Boolean
            Dim motor As LiveTestMotor = Nothing
            If Not _motors.TryGetValue(sesionId, motor) Then Return False
            motor.ActualizarConfig(monto, minConfianza, duracionVelas, horasOperacion, limiteGanancia, limitePerdida)
            RaiseEvent MotorActualizado(sesionId, motor.GetEstado())
            Return True
        End Function
#End Region

#Region "Snapshot de estado"

        ''' <summary>Snapshot JSON de TODOS los motores activos.</summary>
        Public Function GetEstadoGlobal() As JObject
            Dim arr As New JArray()
            For Each kv In _motors
                Try
                    arr.Add(kv.Value.GetEstado())
                Catch
                End Try
            Next
            Return New JObject From {
                {"ok", True},
                {"cantidad", arr.Count},
                {"sesiones_activas", arr}
            }
        End Function

        ''' <summary>Snapshot JSON de un motor especifico.</summary>
        Public Function GetEstadoMotor(sesionId As Integer) As JObject
            Dim motor As LiveTestMotor = Nothing
            If Not _motors.TryGetValue(sesionId, motor) Then
                Return New JObject From {{"ok", False}, {"error", "Sesion " & sesionId & " no esta activa."}}
            End If
            Return motor.GetEstado()
        End Function
#End Region

#Region "Reanudar despues de crash"

        ''' <summary>
        ''' Lee de BD las sesiones live "Abierto" que quedaron sin cerrar y las
        ''' reanuda en memoria. Llamar al iniciar la app.
        ''' </summary>
        Public Async Function ReanudarActivasAsync() As Task(Of Integer)
            Dim activas As List(Of BacktestSesion)
            Try
                activas = Await _repo.GetSesionesLiveActivasAsync()
            Catch ex As Exception
                Log("[LiveCtrl] Error obteniendo sesiones activas: " & ex.Message)
                Return 0
            End Try

            If activas Is Nothing OrElse activas.Count = 0 Then
                Log("[LiveCtrl] No hay sesiones live activas para reanudar.")
                Return 0
            End If

            EstrategiasDinamicasRegistry.Inicializar()

            Dim cuenta As Integer = 0
            For Each ses In activas
                Try
                    Dim defEst = EstrategiasDinamicasRegistry.GetByNombre(ses.Estrategia)
                    If defEst Is Nothing Then
                        Log("[LiveCtrl] Estrategia '" & ses.Estrategia & "' no esta en el registry; sesion #" & ses.Id & " omitida.")
                        Continue For
                    End If

                    Dim cfg As New LiveTestConfig With {
                        .Symbol = ses.Symbol,
                        .Timeframe = ses.Timeframe,
                        .DuracionVelas = ses.DuracionVelas,
                        .PayoutPct = ses.PayoutPct,
                        .MinConfianza = ses.MinConfianza,
                        .MontoOperacion = 1D,
                        .VentanaIndicadores = 100
                    }
                    Dim motor As New LiveTestMotor()
                    motor.ExecuteTrade = Function(body As JObject) ApiExecuteTrade(body)
                    motor.InyectarSesionExistente(ses.Id, cfg, defEst)

                    _motors(ses.Id) = motor

                    If Not _subscriber.EstaSuscrito(ses.Symbol, ses.Timeframe) Then
                        Dim granularity = TimeframeAGranularidad(ses.Timeframe)
                        Await _subscriber.SubscribeAsync(ses.Symbol, ses.Timeframe, granularity)
                    End If

                    Log(String.Format("[LiveCtrl] Sesion #{0} reanudada: {1} {2}/{3}",
                                      ses.Id, ses.Estrategia, ses.Symbol, ses.Timeframe))
                    cuenta += 1
                Catch ex As Exception
                    Log("[LiveCtrl] Error reanudando sesion #" & ses.Id & ": " & ex.Message)
                End Try
            Next
            Return cuenta
        End Function
#End Region

#Region "Dispatch de velas"

        ''' <summary>
        ''' Enruta cada vela cerrada a TODOS los motores activos. Cada motor filtra
        ''' internamente si la vela aplica a su (symbol, timeframe). El procesamiento
        ''' va en Task.Run para no bloquear el thread del WebSocket.
        ''' </summary>
        Private Sub DispatchCandleCerrada(candle As CandleModel, symbol As String, timeframe As String)
            Dim snapshot = _motors.Values.ToList()
            For Each m In snapshot
                Dim motor = m
                Task.Run(Sub()
                             Try
                                 motor.ProcesarCandleCerradaConContexto(candle, symbol, timeframe)
                             Catch ex As Exception
                                 Log("[LiveCtrl] Error procesando vela: " & ex.Message)
                             End Try
                         End Sub)
            Next
        End Sub
#End Region

#Region "Helpers"

        Private Function ApiExecuteTrade(body As JObject) As Task(Of JObject)
            Dim svc = TradingServiceLocator.Current
            If svc Is Nothing Then
                Return Task.FromResult(New JObject From {
                    {"ok", False}, {"error", "TradingService no registrado (MainWindow no inicializo)."}
                })
            End If
            Return svc.ExecuteTradeAsync(body)
        End Function

        Private Shared Function TimeframeAGranularidad(tf As String) As Integer
            Select Case (tf).ToLowerInvariant()
                Case "1m" : Return 60
                Case "5m" : Return 300
                Case "15m" : Return 900
                Case "30m" : Return 1800
                Case "1h" : Return 3600
                Case "4h" : Return 14400
                Case "1d" : Return 86400
                Case Else : Return 60
            End Select
        End Function
#End Region

    End Class

End Namespace

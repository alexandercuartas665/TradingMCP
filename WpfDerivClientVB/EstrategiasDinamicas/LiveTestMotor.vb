' ============================================================
' LiveTestMotor.vb
'
' Motor de prueba en vivo. Mantiene un buffer de velas,
' evalua la estrategia en cada cierre de vela y ejecuta
' trades reales en la cuenta demo de Deriv.
'
' Uso:
'   1. Llamar IniciarAsync(config, estrategia) → retorna sesionId
'   2. Conectar al evento OnCandleCerrada de MainWindow:
'        AddHandler mainWindow.OnCandleCerrada, AddressOf motor.ProcesarCandleCerrada
'   3. Llamar DetenerAsync() para cerrar la sesion
'
' Namespace: WpfDerivClientVB.EstrategiasDinamicas
' ============================================================
Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting

Namespace WpfDerivClientVB.EstrategiasDinamicas

    ''' <summary>
    ''' Representa un trade pendiente de resolucion en live test.
    ''' </summary>
    Friend Class OperacionPendiente
        Public Property OperacionId    As Integer     ' ID en BD
        Public Property EpochEntrada   As Long
        Public Property PrecioEntrada  As Decimal
        Public Property Tipo           As String      ' CALL | PUT
        Public Property VelasSalida    As Integer     ' cuantas velas faltan para cerrar
        Public Property ContractId     As String = "" ' ID contrato Deriv
        Public Property Monto          As Decimal
        Public Property Confianza      As Integer
        Public Property Motivo         As String = ""
        Public Property DatosIndicadores As String = ""
        ' Contexto de velas al momento de la entrada (para perdidas)
        Public Property VelasContexto  As List(Of CandleModel)
        ' True = Deriv maneja la resolucion (no se decrementa VelasSalida ni se computa local).
        ' Usado al reanudar ops abiertas tras un crash: el Trading WS notifica el resultado real.
        Public Property EsExterna As Boolean = False
    End Class

    ''' <summary>
    ''' Motor de live testing. Thread-safe para uso desde el UI thread.
    ''' </summary>
    Public Class LiveTestMotor

        Private ReadOnly _repo As LiveTestRepository

        ' ── Estado de sesion activa ──────────────────────────────────────
        Private _sesionId As Integer = 0
        Private _config As LiveTestConfig = Nothing
        Private _estrategia As EstrategiaJson = Nothing
        Private _activo As Boolean = False
        Private _pausado As Boolean = False    ' Si True: NO se abren nuevas ops (las abiertas siguen resolviendose)

        ' ── Buffer de velas para indicadores ────────────────────────────
        Private ReadOnly _buffer As New List(Of CandleModel)()

        ' ── Trades pendientes de resolucion ──────────────────────────────
        Private ReadOnly _pendientes As New List(Of OperacionPendiente)()
        Private ReadOnly _pendientesLock As New Object()

        ' ── Lock para serializar el procesamiento del motor (Task.Run desde el dispatcher) ─
        Private ReadOnly _processingLock As New Object()

        ' ── Metricas acumuladas de la sesion ────────────────────────────
        Private _totalSenales As Integer = 0
        Private _totalOperaciones As Integer = 0
        Private _nGanadas As Integer = 0
        Private _nPerdidas As Integer = 0
        Private _pnlTotal As Decimal = 0D
        Private _balanceActual As Decimal = 0D
        Private _velasAnalizadas As Integer = 0   ' contador de velas procesadas
        Private _inicioSesion As DateTime = DateTime.MinValue   ' para enforcement de HorasOperacion

        ' ── Callback para ejecutar trades en Deriv ──────────────────────
        ''' <summary>
        ''' Funcion que ejecuta un trade en Deriv. Inyectada por MainWindow.
        ''' Firma: Func(Of JObject body, Task(Of JObject resultado))
        ''' El body tiene: contract_type, symbol, amount, basis, currency, duration, duration_unit, underlying_symbol
        ''' </summary>
        Public Property ExecuteTrade As Func(Of JObject, Task(Of JObject)) = Nothing

        ''' <summary>
        ''' Callback opcional invocado cuando se abre exitosamente un trade live.
        ''' Permite a MainWindow taggear la operacion en el grid (estrategia, sesionId, opId).
        ''' Args: contractId, estrategia, sesionId, opId.
        ''' </summary>
        Public Property OnTradeLiveAbierto As Action(Of String, String, Integer, Integer) = Nothing

        Public Sub New()
            _repo = New LiveTestRepository()
        End Sub

        ' ================================================================
        '  CONTROL DE SESION
        ' ================================================================

        ''' <summary>
        ''' Inicia una nueva sesion. tipoSesion="live" para trading real en Deriv,
        ''' "backtest" para simulacion sobre datos historicos (no contamina ops live).
        ''' </summary>
        Public Async Function IniciarAsync(config As LiveTestConfig, estrategia As EstrategiaJson,
                                            Optional tipoSesion As String = "live") As Task(Of Integer)
            If _activo Then Await DetenerAsync()

            Try
                Await _repo.EnsureTablesExistAsync()
                _sesionId = Await _repo.CrearSesionLiveAsync(config, estrategia.Nombre, tipoSesion)
            Catch ex As Exception
                AppLogger.Warn("[LiveTestMotor] Error iniciando sesion en BD: " & ex.Message)
                _sesionId = -1
            End Try

            _config = config
            _estrategia = estrategia
            _activo = True
            _pausado = False
            _buffer.Clear()
            _pendientes.Clear()

            _totalSenales = 0
            _totalOperaciones = 0
            _nGanadas = 0
            _nPerdidas = 0
            _pnlTotal = 0D
            _balanceActual = 0D
            _velasAnalizadas = 0
            _inicioSesion = DateTime.UtcNow

            AppLogger.Info(String.Format("[LiveTestMotor] Sesion {0} iniciada. Estrategia: {1} | {2} {3}",
                                        _sesionId, estrategia.Nombre, config.Symbol, config.Timeframe))
            Return _sesionId
        End Function

        ''' <summary>
        ''' Reanuda una sesion live previamente activa (usado al recuperarse de un crash).
        ''' En lugar de crear una sesion nueva, se conecta a la existente para seguir
        ''' acumulando ops/metricas alli mismo.
        ''' </summary>
        Public Sub InyectarSesionExistente(sesionId As Integer, config As LiveTestConfig, estrategia As EstrategiaJson)
            If _activo Then Return
            _sesionId = sesionId
            _config = config
            _estrategia = estrategia
            _activo = True
            _pausado = False
            _buffer.Clear()
            _pendientes.Clear()
            _totalSenales = 0
            _totalOperaciones = 0
            _nGanadas = 0
            _nPerdidas = 0
            _pnlTotal = 0D
            _balanceActual = 0D
            _velasAnalizadas = 0
            _inicioSesion = DateTime.UtcNow
            AppLogger.Info(String.Format("[LiveTestMotor] Sesion {0} REANUDADA. Estrategia: {1} | {2} {3}",
                                        _sesionId, estrategia.Nombre, config.Symbol, config.Timeframe))
        End Sub

        ''' <summary>
        ''' Detiene la sesion activa y guarda metricas finales en BD.
        ''' </summary>
        Public Async Function DetenerAsync() As Task
            If Not _activo Then Return
            _activo = False

            ' Cerrar sesion con metricas actuales
            If _sesionId > 0 Then
                Dim winRate = If(_nGanadas + _nPerdidas > 0, CDec(_nGanadas) / CDec(_nGanadas + _nPerdidas), 0D)
                Try
                    Await _repo.CerrarSesionAsync(_sesionId, _totalOperaciones, _totalSenales, winRate, _pnlTotal)
                Catch ex As Exception
                    AppLogger.Warn("[LiveTestMotor] Error cerrando sesion: " & ex.Message)
                End Try
            End If

            AppLogger.Info(String.Format("[LiveTestMotor] Sesion {0} detenida. Ops: {1} | Win: {2} | PnL: {3:F2}",
                                        _sesionId, _totalOperaciones, _nGanadas, _pnlTotal))
            _sesionId = 0
            _config = Nothing
            _estrategia = Nothing
        End Function

        ''' <summary>Indica si hay una sesion de live test activa.</summary>
        Public ReadOnly Property EstaActivo As Boolean
            Get
                Return _activo
            End Get
        End Property

        ''' <summary>True si la estrategia esta pausada (no abre nuevas ops, solo resuelve abiertas).</summary>
        Public ReadOnly Property EstaPausado As Boolean
            Get
                Return _pausado
            End Get
        End Property

        ''' <summary>
        ''' Restaura los contadores de la sesion desde valores historicos (BD). Usado al
        ''' reanudar una sesion existente para que la card muestre las metricas acumuladas.
        ''' </summary>
        Public Sub RestaurarMetricasDesde(ganadas As Integer, perdidas As Integer, pnlTotal As Decimal)
            _nGanadas = ganadas
            _nPerdidas = perdidas
            _totalOperaciones = ganadas + perdidas
            _pnlTotal = pnlTotal
        End Sub

        ''' <summary>
        ''' Agrega una op abierta como "externa" — el motor la cuenta como abierta pero no
        ''' intenta resolverla con velas locales. Su resultado vendra de Deriv via
        ''' NotificarOpResueltaExterna() cuando llegue proposal_open_contract.
        ''' </summary>
        Public Sub AgregarPendienteExterna(operacionId As Integer, contractId As String, tipo As String,
                                            epochEntrada As Long, precioEntrada As Decimal, monto As Decimal)
            If String.IsNullOrEmpty(contractId) Then Return
            SyncLock _pendientesLock
                ' No duplicar si ya esta
                If _pendientes.Any(Function(p) p.ContractId = contractId) Then Return
                _pendientes.Add(New OperacionPendiente With {
                    .OperacionId = operacionId,
                    .ContractId = contractId,
                    .Tipo = tipo,
                    .EpochEntrada = epochEntrada,
                    .PrecioEntrada = precioEntrada,
                    .Monto = monto,
                    .VelasSalida = 0,
                    .EsExterna = True
                })
            End SyncLock
        End Sub

        ''' <summary>
        ''' Notifica al motor que una op (externa o no) ya resolvio en Deriv. Actualiza
        ''' contadores y la quita de _pendientes. Llamado desde ProcesarMensajeTrading.
        ''' </summary>
        Public Sub NotificarOpResueltaExterna(contractId As String, ganada As Boolean, profitLoss As Decimal)
            If String.IsNullOrEmpty(contractId) Then Return
            Dim removido As Boolean = False
            SyncLock _pendientesLock
                Dim toRemove = _pendientes.FirstOrDefault(Function(p) p.ContractId = contractId)
                If toRemove IsNot Nothing Then
                    _pendientes.Remove(toRemove)
                    removido = True
                End If
            End SyncLock
            If Not removido Then Return   ' ya estaba contada o no era nuestra
            If ganada Then
                System.Threading.Interlocked.Increment(_nGanadas)
            Else
                System.Threading.Interlocked.Increment(_nPerdidas)
            End If
            System.Threading.Interlocked.Increment(_totalOperaciones)
            SyncLock _processingLock
                _pnlTotal += profitLoss
            End SyncLock
        End Sub

        ''' <summary>
        ''' Actualiza parametros de la config en runtime sin detener la sesion.
        ''' Pasar Decimal.MinValue / Integer.MinValue para conservar el valor actual.
        ''' </summary>
        Public Sub ActualizarConfig(monto As Decimal, minConfianza As Integer, duracionVelas As Integer,
                                     horasOperacion As Integer, limiteGanancia As Decimal, limitePerdida As Decimal)
            If _config Is Nothing Then Return
            If monto > 0D Then _config.MontoOperacion = monto
            If minConfianza >= 0 Then _config.MinConfianza = minConfianza
            If duracionVelas > 0 Then _config.DuracionVelas = duracionVelas
            If horasOperacion >= 0 Then _config.HorasOperacion = horasOperacion
            If limiteGanancia >= 0D Then _config.LimiteGanancia = limiteGanancia
            If limitePerdida >= 0D Then _config.LimitePerdida = limitePerdida
            AppLogger.Info(String.Format(
                "[LiveTestMotor] Sesion {0} config actualizada: monto={1} conf={2} dur={3} horas={4} limG={5} limP={6}",
                _sesionId, _config.MontoOperacion, _config.MinConfianza, _config.DuracionVelas,
                _config.HorasOperacion, _config.LimiteGanancia, _config.LimitePerdida))
            ' Si los limites ya estan superados, pausar de inmediato
            ChequearLimitesYAutoPausar()
        End Sub

        ''' <summary>
        ''' Verifica los limites de operacion (horas, ganancia, perdida). Si alguno se
        ''' alcanza, pausa el motor automaticamente para no abrir mas trades.
        ''' Se llama antes de cada apertura de trade.
        ''' Devuelve True si esta dentro de limites (puede operar), False si pauso.
        ''' </summary>
        Private Function ChequearLimitesYAutoPausar() As Boolean
            If _config Is Nothing Then Return True
            ' Limite de horas
            If _config.HorasOperacion > 0 AndAlso _inicioSesion <> DateTime.MinValue Then
                Dim transcurridas = (DateTime.UtcNow - _inicioSesion).TotalHours
                If transcurridas >= _config.HorasOperacion Then
                    If Not _pausado Then
                        Pausar()
                        AppLogger.Info(String.Format(
                            "[LiveTestMotor] Sesion {0} AUTO-PAUSADA: limite de {1}h alcanzado ({2:F1}h transcurridas).",
                            _sesionId, _config.HorasOperacion, transcurridas))
                    End If
                    Return False
                End If
            End If
            ' Limite de ganancia
            If _config.LimiteGanancia > 0D AndAlso _pnlTotal >= _config.LimiteGanancia Then
                If Not _pausado Then
                    Pausar()
                    AppLogger.Info(String.Format(
                        "[LiveTestMotor] Sesion {0} AUTO-PAUSADA: limite de ganancia ${1:F2} alcanzado (PnL ${2:F2}).",
                        _sesionId, _config.LimiteGanancia, _pnlTotal))
                End If
                Return False
            End If
            ' Limite de perdida (LimitePerdida es positivo, _pnlTotal negativo)
            If _config.LimitePerdida > 0D AndAlso _pnlTotal <= -_config.LimitePerdida Then
                If Not _pausado Then
                    Pausar()
                    AppLogger.Info(String.Format(
                        "[LiveTestMotor] Sesion {0} AUTO-PAUSADA: limite de perdida -${1:F2} alcanzado (PnL ${2:F2}).",
                        _sesionId, _config.LimitePerdida, _pnlTotal))
                End If
                Return False
            End If
            Return True
        End Function

        ''' <summary>Pausa: deja de abrir nuevas operaciones. Las pendientes siguen resolviendo normal.</summary>
        Public Sub Pausar()
            _pausado = True
            AppLogger.Info(String.Format("[LiveTestMotor] Sesion {0} PAUSADA. No se abriran nuevas ops.", _sesionId))
        End Sub

        ''' <summary>Reanuda: vuelve a evaluar la estrategia y abrir ops cuando hay senal.</summary>
        Public Sub Reanudar()
            _pausado = False
            AppLogger.Info(String.Format("[LiveTestMotor] Sesion {0} REANUDADA.", _sesionId))
        End Sub

        ''' <summary>Retorna el estado actual como JObject para el endpoint /api/dinamicas/live/estado.</summary>
        Public Function GetEstado() As JObject
            Return New JObject From {
                {"activo", _activo},
                {"sesion_id", _sesionId},
                {"estrategia", If(_estrategia IsNot Nothing, _estrategia.Nombre, "")},
                {"symbol", If(_config IsNot Nothing, _config.Symbol, "")},
                {"timeframe", If(_config IsNot Nothing, _config.Timeframe, "")},
                {"buffer_size", _buffer.Count},
                {"pendientes", _pendientes.Count},
                {"total_senales", _totalSenales},
                {"total_ops", _totalOperaciones},
                {"n_ganadas", _nGanadas},
                {"n_perdidas", _nPerdidas},
                {"pnl_total", _pnlTotal},
                {"velas_analizadas", _velasAnalizadas},
                {"pausado", _pausado},
                {"monto_operacion", If(_config IsNot Nothing, _config.MontoOperacion, 0D)},
                {"min_confianza", If(_config IsNot Nothing, _config.MinConfianza, 0)},
                {"duracion_velas", If(_config IsNot Nothing, _config.DuracionVelas, 0)},
                {"payout_pct", If(_config IsNot Nothing, _config.PayoutPct, 0D)},
                {"ventana_indicadores", If(_config IsNot Nothing, _config.VentanaIndicadores, 0)},
                {"horas_operacion", If(_config IsNot Nothing, _config.HorasOperacion, 0)},
                {"limite_ganancia", If(_config IsNot Nothing, _config.LimiteGanancia, 0D)},
                {"limite_perdida", If(_config IsNot Nothing, _config.LimitePerdida, 0D)},
                {"horas_transcurridas", If(_inicioSesion <> DateTime.MinValue, (DateTime.UtcNow - _inicioSesion).TotalHours, 0D)}
            }
        End Function

        ' ================================================================
        '  PROCESAMIENTO DE VELAS
        ' ================================================================

        ''' <summary>
        ''' Procesa una vela cerrada SI coincide con el symbol/timeframe configurado en este motor.
        ''' Permite que varios motores compartan el mismo evento OnCandleCerrada y cada uno
        ''' opere sobre su propio activo (multi-estrategia).
        ''' </summary>
        Public Sub ProcesarCandleCerradaConContexto(candle As CandleModel, symbol As String, timeframe As String)
            If _config Is Nothing Then Return
            If Not String.Equals(_config.Symbol, symbol, StringComparison.OrdinalIgnoreCase) Then Return
            If Not String.Equals(_config.Timeframe, timeframe, StringComparison.OrdinalIgnoreCase) Then Return
            ProcesarCandleCerrada(candle)
        End Sub

        ''' <summary>
        ''' Procesa una vela cerrada. Llamado desde el evento OnCandleCerrada de MainWindow.
        ''' </summary>
        Public Sub ProcesarCandleCerrada(candle As CandleModel)
            If Not _activo OrElse _config Is Nothing OrElse _estrategia Is Nothing Then Return

            ' Contador de velas analizadas (visible en el panel y via /api/dinamicas/live/estado)
            System.Threading.Interlocked.Increment(_velasAnalizadas)

            ' Lock para serializar concurrencia: si dos candles llegan al mismo motor
            ' desde Task.Run paralelos, los serializamos para no corromper _buffer.
            SyncLock _processingLock
                Try
                    ' 1. Agregar al buffer, mantener solo VentanaIndicadores velas
                    _buffer.Add(candle)
                    If _buffer.Count > _config.VentanaIndicadores Then
                        _buffer.RemoveAt(0)
                    End If

                    ' 2. Resolver trades pendientes cuya vela de salida es esta
                    '    (esto SIEMPRE corre, incluso pausado, para que las ops abiertas se cierren)
                    ResolvePendientes(candle)

                    ' 3. Evaluar estrategia si tenemos suficiente historia.
                    '    Si pausado o se alcanzo limite: skip (no abrimos nuevas ops).
                    Dim puedeOperar = ChequearLimitesYAutoPausar()
                    If _pausado OrElse Not puedeOperar Then
                        ' bloqueado — no abrir nuevas
                    ElseIf _buffer.Count >= _config.VentanaIndicadores Then
                        EvaluarYOperar(candle)
                    End If
                Catch ex As Exception
                    AppLogger.Warn("[LiveTestMotor.ProcesarCandleCerrada] Error: " & ex.Message)
                End Try
            End SyncLock
        End Sub

        ' ── Resolver pendientes ──────────────────────────────────────────
        ' Importante: este metodo puede ejecutarse en paralelo con AbrirTradeAsync
        ' (que corre en thread pool y agrega a _pendientes bajo lock).
        ' Para evitar NullReferenceException por iterar la lista mientras se modifica,
        ' tomamos un snapshot bajo lock y trabajamos sobre el.
        Private Sub ResolvePendientes(candleActual As CandleModel)
            Dim snapshot As List(Of OperacionPendiente)
            SyncLock _pendientesLock
                snapshot = _pendientes.ToList()
            End SyncLock

            Dim resueltos As New List(Of OperacionPendiente)()
            For Each pend In snapshot
                If pend Is Nothing Then Continue For   ' defensa extra contra slots nulos
                If pend.EsExterna Then Continue For    ' externa: Deriv la maneja, no decrementar
                pend.VelasSalida -= 1
                If pend.VelasSalida <= 0 Then
                    resueltos.Add(pend)
                End If
            Next

            For Each pend In resueltos
                SyncLock _pendientesLock
                    _pendientes.Remove(pend)
                End SyncLock
                Try
                    ResolverTrade(pend, candleActual)
                Catch ex As Exception
                    AppLogger.Warn("[LiveTestMotor.ResolverTrade] " & ex.Message)
                End Try
            Next
        End Sub

        Private Sub ResolverTrade(pend As OperacionPendiente, velaSalida As CandleModel)
            Dim precioSalida = velaSalida.ClosePrice
            Dim precioEntrada = pend.PrecioEntrada

            Dim estado As String
            Dim pnl As Decimal

            If precioSalida > precioEntrada Then
                estado = If(pend.Tipo = "CALL", "Ganado", "Perdido")
            ElseIf precioSalida < precioEntrada Then
                estado = If(pend.Tipo = "PUT", "Ganado", "Perdido")
            Else
                estado = "Perdido" ' empate = perdida en binarias
            End If

            If estado = "Ganado" Then
                pnl = pend.Monto * (_config.PayoutPct / 100D)
                _nGanadas += 1
            Else
                pnl = -pend.Monto
                _nPerdidas += 1
            End If

            _pnlTotal += pnl
            _balanceActual += pnl

            ' Contexto de velas solo en perdidas
            Dim contextoVelas As String = ""
            If estado = "Perdido" AndAlso pend.VelasContexto IsNot Nothing Then
                Try
                    Dim arr As New JArray()
                    For Each c In pend.VelasContexto
                        arr.Add(New JObject From {
                            {"epoch", c.Epoch}, {"open", c.OpenPrice},
                            {"high", c.High}, {"low", c.Low}, {"close", c.ClosePrice}
                        })
                    Next
                    contextoVelas = arr.ToString(Formatting.None)
                Catch
                End Try
            End If

            AppLogger.Info(String.Format("[LiveTestMotor] RESUELTO {0} #{1}: {2} | PnL: {3:F2} | Balance: {4:F2}",
                                        pend.Tipo, pend.OperacionId, estado, pnl, _balanceActual))

            ' Actualizar BD en background
            Dim opId = pend.OperacionId
            Dim epochS = velaSalida.Epoch
            Dim precioS = precioSalida
            Dim ctx = contextoVelas
            Task.Run(Async Function() As Task
                         Try
                             Await _repo.ActualizarOperacionAsync(opId, precioS, epochS, estado, pnl, _balanceActual, ctx)
                         Catch ex As Exception
                             AppLogger.Warn("[LiveTestMotor] Error actualizando operacion " & opId & ": " & ex.Message)
                         End Try
                     End Function)
        End Sub

        ' ── Evaluar estrategia y abrir trade ────────────────────────────
        Private Sub EvaluarYOperar(candleActual As CandleModel)
            Try
                Dim resultado = _estrategia.Analizar(_buffer)

                If resultado.Senal = "NEUTRAL" Then Return
                _totalSenales += 1

                If resultado.Confianza < _config.MinConfianza Then Return

                ' Capturar contexto de velas para posible perdida
                Dim ventanaContexto = _config.GuardarContextoVelas
                Dim velasContexto As List(Of CandleModel) = Nothing
                If ventanaContexto > 0 Then
                    Dim desde = Math.Max(0, _buffer.Count - ventanaContexto)
                    velasContexto = _buffer.Skip(desde).ToList()
                End If

                Dim datosJson As String = ""
                Try
                    Dim datosObj As New JObject()
                    For Each kv In resultado.Datos
                        datosObj(kv.Key) = kv.Value
                    Next
                    datosJson = datosObj.ToString(Formatting.None)
                Catch
                End Try

                _totalOperaciones += 1

                ' Crear objeto de operacion pendiente provisional (sin opId hasta que BD responda)
                Dim pend As New OperacionPendiente() With {
                    .EpochEntrada = candleActual.Epoch,
                    .PrecioEntrada = candleActual.ClosePrice,
                    .Tipo = resultado.Senal,
                    .VelasSalida = _config.DuracionVelas,
                    .Monto = _config.MontoOperacion,
                    .Confianza = resultado.Confianza,
                    .Motivo = resultado.Motivo,
                    .DatosIndicadores = datosJson,
                    .VelasContexto = velasContexto
                }

                AppLogger.Info(String.Format("[LiveTestMotor] SENAL {0} | Conf: {1}% | Monto: {2} | {3}",
                                            resultado.Senal, resultado.Confianza,
                                            _config.MontoOperacion, resultado.Motivo))

                ' Ejecutar en background para no bloquear el procesamiento de velas
                Task.Run(Async Function() As Task
                             Await AbrirTradeAsync(pend)
                         End Function)

            Catch ex As Exception
                AppLogger.Warn("[LiveTestMotor.EvaluarYOperar] Error: " & ex.Message)
            End Try
        End Sub

        Private Async Function AbrirTradeAsync(pend As OperacionPendiente) As Task
            ' 1. Guardar operacion provisional en BD
            Dim opProv As New BacktestOperacion() With {
                .SesionId = _sesionId,
                .CuentaNombre = "live",
                .Tipo = pend.Tipo,
                .EpochEntrada = pend.EpochEntrada,
                .PrecioEntrada = pend.PrecioEntrada,
                .EpochSalida = 0,
                .PrecioSalida = 0,
                .Monto = pend.Monto,
                .PayoutPct = _config.PayoutPct,
                .Estado = "Pendiente",
                .ProfitLoss = 0,
                .BalancePost = _balanceActual,
                .Confianza = pend.Confianza,
                .MotivoSenal = pend.Motivo,
                .DatosIndicadores = pend.DatosIndicadores
            }

            Try
                pend.OperacionId = Await _repo.GuardarOperacionAsync(opProv)
            Catch ex As Exception
                AppLogger.Warn("[LiveTestMotor] Error guardando operacion en BD: " & ex.Message)
                pend.OperacionId = 0
            End Try

            ' 2. Agregar a pendientes ANTES de ejecutar el trade (para que la resolucion no se pierda)
            SyncLock _pendientesLock
                _pendientes.Add(pend)
            End SyncLock

            ' 3. Ejecutar trade en Deriv (si hay callback configurado)
            If ExecuteTrade IsNot Nothing Then
                Try
                    ' Campos con prefijo _meta_ son metadata interna que ApiExecuteTrade
                    ' extraera ANTES de enviar a Deriv, y los pasara al handler "buy"
                    ' para que taggee la op en el grid de forma sincrona (sin race).
                    Dim body As New JObject() From {
                        {"contract_type", pend.Tipo},
                        {"symbol", _config.Symbol},
                        {"amount", CDbl(pend.Monto)},
                        {"basis", "stake"},
                        {"currency", "USD"},
                        {"duration", _config.DuracionVelas},
                        {"duration_unit", "t"},
                        {"underlying_symbol", _config.Symbol},
                        {"_meta_estrategia", If(_estrategia IsNot Nothing, _estrategia.Nombre, "")},
                        {"_meta_sesion_id", _sesionId},
                        {"_meta_op_id", pend.OperacionId}
                    }

                    Dim tradeResult = Await ExecuteTrade(body)
                    Dim contractId = tradeResult?("contract_id")?.ToString()
                    If Not String.IsNullOrWhiteSpace(contractId) Then
                        pend.ContractId = contractId
                        ' CRITICAL: persistir el contract_id en BD para que la reconciliacion
                        ' tras reinicio funcione (la query filtra contract_id IS NOT NULL).
                        Try
                            Await _repo.ActualizarContractIdAsync(pend.OperacionId, contractId)
                        Catch ex As Exception
                            AppLogger.Warn("[LiveTestMotor] No se pudo guardar contract_id: " & ex.Message)
                        End Try
                        ' Notificar a MainWindow para que taggee la op en el grid
                        ' como "live" con el nombre de la estrategia y sesion
                        Try
                            OnTradeLiveAbierto?.Invoke(
                                contractId,
                                If(_estrategia IsNot Nothing, _estrategia.Nombre, ""),
                                _sesionId,
                                pend.OperacionId)
                        Catch
                        End Try
                    End If

                    Dim okVal = tradeResult?("ok")?.ToObject(Of Boolean)()
                    AppLogger.Info(String.Format("[LiveTestMotor] Trade Deriv: {0} | ContractId: {1}",
                                                If(okVal, "OK", "FALLO"), If(contractId, "N/A")))
                Catch ex As Exception
                    AppLogger.Warn("[LiveTestMotor] Error ejecutando trade en Deriv: " & ex.Message)
                End Try
            Else
                AppLogger.Warn("[LiveTestMotor] ExecuteTrade no configurado. Trade registrado solo en BD (sin ejecucion real).")
            End If
        End Function

    End Class

End Namespace

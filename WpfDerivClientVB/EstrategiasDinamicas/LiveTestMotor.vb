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
    End Class

    ''' <summary>
    ''' Motor de live testing. Thread-safe para uso desde el UI thread.
    ''' </summary>
    Public Class LiveTestMotor

        Private ReadOnly _repo As LiveTestRepository

        ' ── Estado de sesion activa ──────────────────────────────────────
        Private _sesionId     As Integer = 0
        Private _config       As LiveTestConfig = Nothing
        Private _estrategia   As EstrategiaJson = Nothing
        Private _activo       As Boolean = False

        ' ── Buffer de velas para indicadores ────────────────────────────
        Private ReadOnly _buffer As New List(Of CandleModel)()

        ' ── Trades pendientes de resolucion ──────────────────────────────
        Private ReadOnly _pendientes     As New List(Of OperacionPendiente)()
        Private ReadOnly _pendientesLock As New Object()

        ' ── Metricas acumuladas de la sesion ────────────────────────────
        Private _totalSenales     As Integer = 0
        Private _totalOperaciones As Integer = 0
        Private _nGanadas         As Integer = 0
        Private _nPerdidas        As Integer = 0
        Private _pnlTotal         As Decimal = 0D
        Private _balanceActual    As Decimal = 0D

        ' ── Callback para ejecutar trades en Deriv ──────────────────────
        ''' <summary>
        ''' Funcion que ejecuta un trade en Deriv. Inyectada por MainWindow.
        ''' Firma: Func(Of JObject body, Task(Of JObject resultado))
        ''' El body tiene: contract_type, symbol, amount, basis, currency, duration, duration_unit, underlying_symbol
        ''' </summary>
        Public Property ExecuteTrade As Func(Of JObject, Task(Of JObject)) = Nothing

        Public Sub New()
            _repo = New LiveTestRepository()
        End Sub

        ' ================================================================
        '  CONTROL DE SESION
        ' ================================================================

        ''' <summary>
        ''' Inicia una nueva sesion de live testing. Retorna el ID de sesion.
        ''' </summary>
        Public Async Function IniciarAsync(config As LiveTestConfig, estrategia As EstrategiaJson) As Task(Of Integer)
            If _activo Then Await DetenerAsync()

            Try
                Await _repo.EnsureTablesExistAsync()
                _sesionId = Await _repo.CrearSesionLiveAsync(config, estrategia.Nombre)
            Catch ex As Exception
                AppLogger.Warn("[LiveTestMotor] Error iniciando sesion en BD: " & ex.Message)
                _sesionId = -1
            End Try

            _config   = config
            _estrategia = estrategia
            _activo   = True
            _buffer.Clear()
            _pendientes.Clear()

            _totalSenales     = 0
            _totalOperaciones = 0
            _nGanadas         = 0
            _nPerdidas        = 0
            _pnlTotal         = 0D
            _balanceActual    = 0D

            AppLogger.Info(String.Format("[LiveTestMotor] Sesion {0} iniciada. Estrategia: {1} | {2} {3}",
                                        _sesionId, estrategia.Nombre, config.Symbol, config.Timeframe))
            Return _sesionId
        End Function

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
            _sesionId   = 0
            _config     = Nothing
            _estrategia = Nothing
        End Function

        ''' <summary>Indica si hay una sesion de live test activa.</summary>
        Public ReadOnly Property EstaActivo As Boolean
            Get
                Return _activo
            End Get
        End Property

        ''' <summary>Retorna el estado actual como JObject para el endpoint /api/dinamicas/live/estado.</summary>
        Public Function GetEstado() As JObject
            Return New JObject From {
                {"activo",          _activo},
                {"sesion_id",       _sesionId},
                {"estrategia",      If(_estrategia IsNot Nothing, _estrategia.Nombre, "")},
                {"symbol",          If(_config IsNot Nothing, _config.Symbol, "")},
                {"timeframe",       If(_config IsNot Nothing, _config.Timeframe, "")},
                {"buffer_size",     _buffer.Count},
                {"pendientes",      _pendientes.Count},
                {"total_senales",   _totalSenales},
                {"total_ops",       _totalOperaciones},
                {"n_ganadas",       _nGanadas},
                {"n_perdidas",      _nPerdidas},
                {"pnl_total",       _pnlTotal}
            }
        End Function

        ' ================================================================
        '  PROCESAMIENTO DE VELAS
        ' ================================================================

        ''' <summary>
        ''' Procesa una vela cerrada. Llamado desde el evento OnCandleCerrada de MainWindow.
        ''' </summary>
        Public Sub ProcesarCandleCerrada(candle As CandleModel)
            If Not _activo OrElse _config Is Nothing OrElse _estrategia Is Nothing Then Return

            Try
                ' 1. Agregar al buffer, mantener solo VentanaIndicadores velas
                _buffer.Add(candle)
                If _buffer.Count > _config.VentanaIndicadores Then
                    _buffer.RemoveAt(0)
                End If

                ' 2. Resolver trades pendientes cuya vela de salida es esta
                ResolvePendientes(candle)

                ' 3. Evaluar estrategia si tenemos suficiente historia
                If _buffer.Count >= _config.VentanaIndicadores Then
                    EvaluarYOperar(candle)
                End If
            Catch ex As Exception
                AppLogger.Warn("[LiveTestMotor.ProcesarCandleCerrada] Error: " & ex.Message)
            End Try
        End Sub

        ' ── Resolver pendientes ──────────────────────────────────────────
        Private Sub ResolvePendientes(candleActual As CandleModel)
            Dim resueltos As New List(Of OperacionPendiente)()

            For Each pend In _pendientes
                pend.VelasSalida -= 1

                If pend.VelasSalida <= 0 Then
                    resueltos.Add(pend)
                End If
            Next

            For Each pend In resueltos
                _pendientes.Remove(pend)
                ResolverTrade(pend, candleActual)
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

            _pnlTotal     += pnl
            _balanceActual += pnl

            ' Contexto de velas solo en perdidas
            Dim contextoVelas As String = ""
            If estado = "Perdido" AndAlso pend.VelasContexto IsNot Nothing Then
                Try
                    Dim arr As New JArray()
                    For Each c In pend.VelasContexto
                        arr.Add(New JObject From {
                            {"epoch", c.Epoch}, {"open", c.OpenPrice},
                            {"high",  c.High},  {"low",  c.Low}, {"close", c.ClosePrice}
                        })
                    Next
                    contextoVelas = arr.ToString(Formatting.None)
                Catch
                End Try
            End If

            AppLogger.Info(String.Format("[LiveTestMotor] RESUELTO {0} #{1}: {2} | PnL: {3:F2} | Balance: {4:F2}",
                                        pend.Tipo, pend.OperacionId, estado, pnl, _balanceActual))

            ' Actualizar BD en background
            Dim opId    = pend.OperacionId
            Dim epochS  = velaSalida.Epoch
            Dim precioS = precioSalida
            Dim ctx     = contextoVelas
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
                    .EpochEntrada    = candleActual.Epoch,
                    .PrecioEntrada   = candleActual.ClosePrice,
                    .Tipo            = resultado.Senal,
                    .VelasSalida     = _config.DuracionVelas,
                    .Monto           = _config.MontoOperacion,
                    .Confianza       = resultado.Confianza,
                    .Motivo          = resultado.Motivo,
                    .DatosIndicadores = datosJson,
                    .VelasContexto   = velasContexto
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
                .SesionId         = _sesionId,
                .CuentaNombre     = "live",
                .Tipo             = pend.Tipo,
                .EpochEntrada     = pend.EpochEntrada,
                .PrecioEntrada    = pend.PrecioEntrada,
                .EpochSalida      = 0,
                .PrecioSalida     = 0,
                .Monto            = pend.Monto,
                .PayoutPct        = _config.PayoutPct,
                .Estado           = "Pendiente",
                .ProfitLoss       = 0,
                .BalancePost      = _balanceActual,
                .Confianza        = pend.Confianza,
                .MotivoSenal      = pend.Motivo,
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
                    Dim body As New JObject() From {
                        {"contract_type",   pend.Tipo},
                        {"symbol",          _config.Symbol},
                        {"amount",          CDbl(pend.Monto)},
                        {"basis",           "stake"},
                        {"currency",        "USD"},
                        {"duration",        _config.DuracionVelas},
                        {"duration_unit",   "t"},
                        {"underlying_symbol", _config.Symbol}
                    }

                    Dim tradeResult = Await ExecuteTrade(body)
                    Dim contractId  = tradeResult?("contract_id")?.ToString()
                    If Not String.IsNullOrWhiteSpace(contractId) Then
                        pend.ContractId = contractId
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

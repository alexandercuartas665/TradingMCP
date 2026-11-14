' ============================================================
' DerivTradingService.vb
'
' Conexion de trading con Deriv SIN interfaz grafica. Antes toda
' esta logica vivia dentro de MainWindow (ventana "Pruebas"): el
' modo agente tenia que abrir una ventana para operar y nada se
' podia probar. Ahora:
'
'   - Este servicio (singleton) mantiene el WebSocket de trading:
'     OTP, conexion, reconexion automatica y ping.
'   - Todas las respuestas de Deriv se correlacionan por req_id:
'     compras automaticas, cotizaciones/compras manuales y ventas.
'   - Procesa los mensajes (saldo, buy, proposal_open_contract,
'     sell, errores), persiste en BD, avisa a los motores live y
'     al gestor de riesgo.
'   - Implementa ITradingService y se registra al arrancar la app
'     (App.OnStartup): Servidor Live, API y SignalR operan sin
'     ninguna ventana abierta.
'   - La UI (MainWindow) solo se suscribe a los eventos para
'     mostrar el estado. Los eventos se disparan desde hilos de
'     fondo: la UI debe pasar a su Dispatcher.
' ============================================================
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Linq
Imports System.Net.WebSockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas

Namespace WpfDerivClientVB.Servidor

    Public NotInheritable Class DerivTradingService
        Implements ITradingService

#Region "Singleton"
        Private Shared ReadOnly _instancia As New Lazy(Of DerivTradingService)(Function() New DerivTradingService())
        Public Shared ReadOnly Property Instance As DerivTradingService
            Get
                Return _instancia.Value
            End Get
        End Property
        Private Sub New()
        End Sub
#End Region

#Region "Eventos (se disparan desde hilos de fondo)"
        Public Event Log(mensaje As String)
        Public Event EstadoConexion(conectado As Boolean, texto As String)
        Public Event SaldoActualizado(saldo As Decimal, moneda As String)
        Public Event OperacionAgregada(op As OperacionModel)
        Public Event OperacionQuitada(op As OperacionModel)
        Public Event OperacionCerrada(op As OperacionModel)
        Public Event MensajeOperacion(texto As String, esError As Boolean)
        ''' <summary>
        ''' Las suscripciones de velas se pierden al reconectar o desconectar el WebSocket:
        ''' quien las use debe volver a llamar SuscribirVelasAsync (si EstaConectado).
        ''' </summary>
        Public Event SuscripcionesVelasPerdidas()
#End Region

#Region "Estado"
        Private _ws As ClientWebSocket
        Private _cts As CancellationTokenSource
        Private _cliente As ClientModel
        Private _creds As ClientDerivCredentials
        Private _esDemo As Boolean = True
        Private _saldo As Decimal? = Nothing
        Private _moneda As String = "USD"

        Private ReadOnly _ops As New List(Of OperacionModel)()
        Private ReadOnly _opsLock As New Object()

        ''' <summary>
        ''' Busca motores live que no son del LiveStrategiesController (los de la pestana
        ''' Estrategias de MainWindow, que viven en su TradingApiServer). Lo asigna MainWindow.
        ''' </summary>
        Public Property BuscarMotorExterno As Func(Of Integer, LiveTestMotor) = Nothing

        Public ReadOnly Property EstaConectado As Boolean
            Get
                Dim ws = _ws
                Return ws IsNot Nothing AndAlso ws.State = WebSocketState.Open
            End Get
        End Property

        Public ReadOnly Property EsDemo As Boolean
            Get
                Return _esDemo
            End Get
        End Property

        Public ReadOnly Property Cliente As ClientModel
            Get
                Return _cliente
            End Get
        End Property

        Public ReadOnly Property TextoSaldo As String
            Get
                If Not _saldo.HasValue Then Return "--"
                Return String.Format("{0} {1:N2}", _moneda, _saldo.Value)
            End Get
        End Property

        ''' <summary>Copia de las operaciones conocidas (mas recientes primero).</summary>
        Public Function ObtenerOperaciones() As List(Of OperacionModel)
            SyncLock _opsLock
                Return _ops.ToList()
            End SyncLock
        End Function

        Private Function BuscarOp(contractId As String) As OperacionModel
            If String.IsNullOrEmpty(contractId) Then Return Nothing
            SyncLock _opsLock
                Return _ops.FirstOrDefault(Function(o) o.ContractId = contractId)
            End SyncLock
        End Function

        Private Sub EscribirLog(mensaje As String)
            Dim linea = String.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss"), mensaje)
            AppLogger.Info("[Trading] " & mensaje)
            Try
                RaiseEvent Log(linea)
            Catch
            End Try
        End Sub

        Private Sub Avisar(texto As String, Optional esError As Boolean = False)
            Try
                RaiseEvent MensajeOperacion(texto, esError)
            Catch
            End Try
        End Sub
#End Region

#Region "Conexion"
        ''' <summary>
        ''' Conecta el WebSocket de trading para el cliente y tipo de cuenta indicados.
        ''' Devuelve "" si conecto o el motivo del fallo (sin mostrar dialogos).
        ''' </summary>
        Public Async Function ConectarAsync(cliente As ClientModel, creds As ClientDerivCredentials,
                                            esDemo As Boolean) As Task(Of String)
            If cliente Is Nothing Then Return "Selecciona un cliente primero."
            If creds Is Nothing Then Return "El cliente no tiene credenciales Deriv configuradas."

            Desconectar(silencioso:=True)
            _cliente = cliente
            _creds = creds
            _esDemo = esDemo
            _cts = New CancellationTokenSource()
            RaiseEstado(False, "Conectando...")

            Dim err = Await AbrirWebSocketAsync()
            If err <> "" Then
                RaiseEstado(False, "Error de conexion")
                Return err
            End If

#Disable Warning BC42358
            Task.Run(Function() EscucharAsync(_cts.Token))
            Task.Run(Function() PingLoopAsync(_cts.Token))
#Enable Warning BC42358

            ' Operaciones live que quedaron Pendiente/Abierto (crash/cierre) + reconciliacion
            Await RecuperarOperacionesLiveAsync()
#Disable Warning BC42358
            ReconciliarOperacionesAbiertasAsync()
#Enable Warning BC42358
            Return ""
        End Function

        ''' <summary>Cierra la conexion (sin reconexion automatica).</summary>
        Public Sub Desconectar(Optional silencioso As Boolean = False)
            Dim cts = _cts
            If cts IsNot Nothing Then
                Try
                    cts.Cancel()
                Catch
                End Try
            End If
            Dim ws = _ws
            _ws = Nothing
            If ws IsNot Nothing Then
                Try
                    ws.Dispose()
                Catch
                End Try
            End If
            _saldo = Nothing
            OlvidarSuscripcionesVelas()
            If Not silencioso Then RaiseEstado(False, "Desconectado")
        End Sub

        Private Sub RaiseEstado(conectado As Boolean, texto As String)
            Try
                RaiseEvent EstadoConexion(conectado, texto)
            Catch
            End Try
        End Sub

        ''' <summary>OTP + ConnectAsync. Devuelve "" si conecto o el motivo del fallo.</summary>
        Private Async Function AbrirWebSocketAsync() As Task(Of String)
            Try
                Dim accountId = If(_esDemo, _creds.AccountIdVirtual, _creds.AccountIdReal)
                Dim appId = If(_esDemo, _creds.AppIdVirtual, _creds.AppIdReal)
                Dim token = If(_esDemo, _creds.ApiTokenVirtual, _creds.ApiTokenReal)
                If String.IsNullOrWhiteSpace(accountId) OrElse String.IsNullOrWhiteSpace(token) Then
                    Return "Faltan Account ID o Token para la cuenta " & If(_esDemo, "Demo", "Real") & "."
                End If

                Dim otpUrl = String.Format("https://api.derivws.com/trading/v1/options/accounts/{0}/otp", accountId)
                Dim body = Await ObtenerOtpConRetryAsync(otpUrl, token, appId)
                If String.IsNullOrEmpty(body) Then Return "No se pudo obtener OTP de Deriv tras 3 intentos."
                Dim otp = JObject.Parse(body)
                If otp("data") Is Nothing OrElse otp("data")("url") Is Nothing Then
                    Return "Respuesta OTP inesperada de Deriv."
                End If

                Dim ws As New ClientWebSocket()
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15)
                Await ws.ConnectAsync(New Uri(otp("data")("url").ToString()), _cts.Token)
                Dim anterior = _ws
                _ws = ws
                If anterior IsNot Nothing AndAlso Not ReferenceEquals(anterior, ws) Then
                    Try
                        anterior.Dispose()
                    Catch
                    End Try
                End If

                RaiseEstado(True, If(_esDemo, "Demo", "Real") & " - Conectado")
                Await EnviarAsync(New JObject From {{"balance", 1}, {"subscribe", 1}})
                OlvidarSuscripcionesVelas()
                EscribirLog(String.Format("WebSocket trading conectado ({0}).", If(_esDemo, "Demo", "Real")))
                Return ""
            Catch ex As OperationCanceledException
                Return "Conexion cancelada."
            Catch ex As Exception
                Return "Error al conectar: " & ex.Message
            End Try
        End Function

        ''' <summary>POST al endpoint OTP con 3 intentos (0s, 2s, 5s).</summary>
        Private Async Function ObtenerOtpConRetryAsync(otpUrl As String, token As String, appId As String) As Task(Of String)
            Dim demoras() As Integer = {0, 2000, 5000}
            For intento = 0 To demoras.Length - 1
                If demoras(intento) > 0 Then
                    Try
                        Await Task.Delay(demoras(intento), _cts.Token)
                    Catch
                        Return ""
                    End Try
                End If
                Try
                    Using client As New System.Net.Http.HttpClient()
                        client.Timeout = TimeSpan.FromSeconds(15)
                        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " & token)
                        client.DefaultRequestHeaders.TryAddWithoutValidation("deriv-app-id", appId)
                        Using content As New System.Net.Http.StringContent("", Encoding.UTF8, "application/json")
                            Using resp = Await client.PostAsync(otpUrl, content, _cts.Token)
                                Dim body = Await resp.Content.ReadAsStringAsync()
                                If resp.IsSuccessStatusCode Then
                                    If intento > 0 Then EscribirLog(String.Format("OTP recuperado en intento #{0}.", intento + 1))
                                    Return body
                                End If
                                EscribirLog(String.Format("OTP intento #{0} fallo: HTTP {1}", intento + 1, CInt(resp.StatusCode)))
                            End Using
                        End Using
                    End Using
                Catch ex As OperationCanceledException
                    Return ""
                Catch ex As Exception
                    EscribirLog(String.Format("OTP intento #{0} error: {1}", intento + 1, ex.Message))
                End Try
            Next
            Return ""
        End Function

        ''' <summary>Bucle de recepcion con reconexion automatica (3s x3, luego 10s).</summary>
        Private Async Function EscucharAsync(ct As CancellationToken) As Task
            Dim buffer(32767) As Byte
            Dim intentos As Integer = 0
            Do While Not ct.IsCancellationRequested
                Try
                    Dim ws = _ws
                    Do While ws IsNot Nothing AndAlso ws.State = WebSocketState.Open AndAlso Not ct.IsCancellationRequested
                        Dim sb As New StringBuilder()
                        Dim result As WebSocketReceiveResult
                        Do
                            result = Await ws.ReceiveAsync(New ArraySegment(Of Byte)(buffer), ct)
                            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count))
                        Loop While Not result.EndOfMessage
                        If result.MessageType = WebSocketMessageType.Close Then
                            EscribirLog("Servidor cerro la conexion (cierre limpio).")
                            Exit Do
                        End If
                        intentos = 0
                        Try
                            ProcesarMensaje(sb.ToString())
                        Catch ex As Exception
                            AppLogger.Error("[Trading] Error procesando mensaje de Deriv", ex)
                        End Try
                    Loop
                Catch ex As OperationCanceledException
                    Exit Do
                Catch ex As Exception
                    If Not ct.IsCancellationRequested Then EscribirLog("Conexion caida: " & ex.Message)
                End Try
                If ct.IsCancellationRequested Then Exit Do

                intentos += 1
                Dim espera = If(intentos <= 3, 3, 10)
                EscribirLog(String.Format("Reconectando en {0}s... (intento #{1})", espera, intentos))
                RaiseEstado(False, String.Format("Reconectando... (#{0})", intentos))
                Try
                    Await Task.Delay(espera * 1000, ct)
                Catch
                    Exit Do
                End Try
                If ct.IsCancellationRequested Then Exit Do

                Dim err = Await AbrirWebSocketAsync()
                If err = "" Then
                    intentos = 0
                    EscribirLog("Reconexion exitosa.")
                    For Each op In ObtenerOperaciones().Where(Function(o) o.Estado = "Abierto" AndAlso Not String.IsNullOrEmpty(o.ContractId))
                        SuscribirContrato(op.ContractId)
                    Next
                Else
                    EscribirLog(String.Format("Reconexion #{0} fallida: {1}", intentos, err))
                End If
            Loop
        End Function

        ''' <summary>Ping de aplicacion cada 20s (ademas del keepalive del protocolo).</summary>
        Private Async Function PingLoopAsync(ct As CancellationToken) As Task
            Dim ping = Encoding.UTF8.GetBytes("{""ping"":1}")
            Do While Not ct.IsCancellationRequested
                Try
                    Await Task.Delay(20000, ct)
                    If EstaConectado Then Await EnviarBytesAsync(ping)
                Catch ex As OperationCanceledException
                    Exit Do
                Catch
                End Try
            Loop
        End Function
#End Region

#Region "Envio (canal unico) y correlacion por req_id"
        ' ClientWebSocket NO admite dos SendAsync simultaneos: todo pasa por este semaforo.
        Private ReadOnly _envioLock As New SemaphoreSlim(1, 1)

        Private Async Function EnviarBytesAsync(bytes As Byte()) As Task
            Dim ws = _ws
            If ws Is Nothing OrElse ws.State <> WebSocketState.Open Then
                Throw New InvalidOperationException("WebSocket de trading no conectado.")
            End If
            Await _envioLock.WaitAsync()
            Try
                Await ws.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _cts.Token)
            Finally
                _envioLock.Release()
            End Try
        End Function

        Private Function EnviarAsync(req As JObject) As Task
            Return EnviarBytesAsync(Encoding.UTF8.GetBytes(req.ToString(Newtonsoft.Json.Formatting.None)))
        End Function

        Private Sub EnviarSinEsperar(req As JObject)
            EnviarAsync(req).ContinueWith(
                Sub(t) AppLogger.Error("[Trading] Envio al WebSocket fallo", t.Exception?.GetBaseException()),
                TaskContinuationOptions.OnlyOnFaulted)
        End Sub

        Private Enum TipoPendiente
            CompraAutomatica   ' proposal -> buy automatico (con metadata de estrategia)
            Cotizacion         ' proposal manual: devuelve la cotizacion
            Compra             ' buy manual de una cotizacion ya obtenida
            Venta              ' sell
            Consulta           ' consulta de solo lectura: devuelve la respuesta cruda
        End Enum

        ' Unicos tipos de mensaje permitidos en ConsultarAsync: ninguno mueve dinero.
        Private Shared ReadOnly ConsultasPermitidas As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "ticks_history", "active_symbols", "contracts_for", "time", "website_status", "trading_times"
        }

        ''' <summary>
        ''' Consulta de SOLO LECTURA por la conexion autenticada (ticks_history, active_symbols,
        ''' contracts_for, time...). Lista blanca: nunca permite buy/sell/proposal.
        ''' Devuelve la respuesta cruda de Deriv o {ok:false, error}.
        ''' </summary>
        Public Async Function ConsultarAsync(req As JObject, Optional timeoutMs As Integer = 20000) As Task(Of JObject)
            If req Is Nothing Then Return New JObject From {{"ok", False}, {"error", "Peticion vacia."}}
            Dim tipo = req.Properties().Select(Function(prop) prop.Name).FirstOrDefault(Function(n) ConsultasPermitidas.Contains(n))
            If tipo Is Nothing OrElse req.Properties().Any(Function(prop) prop.Name = "buy" OrElse prop.Name = "sell" OrElse prop.Name = "proposal" OrElse prop.Name = "subscribe") Then
                Return New JObject From {{"ok", False}, {"error", "Solo se permiten consultas de lectura: " & String.Join(", ", ConsultasPermitidas)}}
            End If
            If Not EstaConectado Then Return New JObject From {{"ok", False}, {"error", "Trading no conectado."}}
            Dim p = NuevoPendiente(TipoPendiente.Consulta)
            Return Await EnviarYEsperarAsync(p, CType(req.DeepClone(), JObject), timeoutMs)
        End Function

        ' Suscripciones de velas en vivo: id de suscripcion de Deriv -> callback con el "ohlc"
        Private ReadOnly _suscripcionesVelas As New ConcurrentDictionary(Of String, Action(Of JObject))()

        ''' <summary>
        ''' Suscribe velas en vivo (ticks_history + subscribe) por la conexion autenticada.
        ''' Devuelve la respuesta de Deriv ({ok, candles, subscription:{id}}) o {ok:false, error}.
        ''' Cada tick llega a onOhlc (hilo del WebSocket: no tocar la UI directamente).
        ''' Al reconectar se pierden: ver SuscripcionesVelasPerdidas.
        ''' </summary>
        Public Async Function SuscribirVelasAsync(symbol As String, granularidad As Integer, cantidad As Integer,
                                                  onOhlc As Action(Of JObject)) As Task(Of JObject)
            If Not EstaConectado Then Return New JObject From {{"ok", False}, {"error", "Sin conexion a Deriv: conecta la cuenta primero."}}
            Dim p = NuevoPendiente(TipoPendiente.Consulta)
            Dim r = Await EnviarYEsperarAsync(p, New JObject From {
                {"ticks_history", symbol}, {"style", "candles"}, {"granularity", granularidad},
                {"count", cantidad}, {"end", "latest"}, {"adjust_start_time", 1}, {"subscribe", 1}
            }, 20000)
            Dim subId = r?("subscription")?("id")?.ToString()
            If r IsNot Nothing AndAlso r("ok") IsNot Nothing AndAlso CBool(r("ok")) AndAlso Not String.IsNullOrEmpty(subId) Then
                _suscripcionesVelas(subId) = onOhlc
            End If
            Return r
        End Function

        ''' <summary>Cancela una suscripcion de velas (forget en Deriv).</summary>
        Public Async Function CancelarSuscripcionVelasAsync(subId As String) As Task
            If String.IsNullOrEmpty(subId) Then Return
            Dim quitado As Action(Of JObject) = Nothing
            If Not _suscripcionesVelas.TryRemove(subId, quitado) Then Return
            If Not EstaConectado Then Return
            Try
                Await EnviarAsync(New JObject From {{"forget", subId}})
            Catch ex As Exception
                EscribirLog("No se pudo cancelar la suscripcion de velas: " & ex.Message)
            End Try
        End Function

        Private Sub OlvidarSuscripcionesVelas()
            _suscripcionesVelas.Clear()
            Try
                RaiseEvent SuscripcionesVelasPerdidas()
            Catch
            End Try
        End Sub

        Private Class Pendiente
            Public Property ReqId As Integer
            Public Property Tipo As TipoPendiente
            Public Property Tcs As New TaskCompletionSource(Of JObject)(TaskCreationOptions.RunContinuationsAsynchronously)
            Public Property Monto As Decimal
            Public Property Estrategia As String = ""
            Public Property SesionId As Integer
            Public Property OperacionLiveId As Integer
            Public Property CreadaUtc As DateTime = DateTime.UtcNow
        End Class

        Private _pocVaciosLogueados As Integer = 0
        Private _erroresLogueados As Integer = 0
        Private _rateLimitHastaUtc As DateTime = DateTime.MinValue
        Private ReadOnly _pendientes As New ConcurrentDictionary(Of Integer, Pendiente)()
        Private _siguienteReqId As Integer = 100000

        Private Function NuevoPendiente(tipo As TipoPendiente, Optional monto As Decimal = 0D,
                                        Optional meta As JObject = Nothing) As Pendiente
            Dim limite = DateTime.UtcNow.AddMinutes(-2)
            For Each kv In _pendientes
                If kv.Value.CreadaUtc < limite Then
                    Dim viejo As Pendiente = Nothing
                    If _pendientes.TryRemove(kv.Key, viejo) Then viejo.Tcs.TrySetResult(Nothing)
                End If
            Next
            Dim p As New Pendiente With {
                .ReqId = Interlocked.Increment(_siguienteReqId),
                .Tipo = tipo,
                .Monto = monto,
                .Estrategia = If(meta?("_meta_estrategia")?.ToString(), ""),
                .SesionId = If(meta?("_meta_sesion_id") IsNot Nothing, CInt(meta("_meta_sesion_id")), 0),
                .OperacionLiveId = If(meta?("_meta_op_id") IsNot Nothing, CInt(meta("_meta_op_id")), 0)
            }
            _pendientes(p.ReqId) = p
            Return p
        End Function

        Private Shared Function LeerReqId(obj As JObject) As Integer
            Dim tok = If(obj("req_id"), obj("echo_req")?("req_id"))
            Dim v As Integer
            If tok IsNot Nothing AndAlso Integer.TryParse(tok.ToString(), v) Then Return v
            Return 0
        End Function

        Private Function BuscarPendiente(obj As JObject) As Pendiente
            Dim id = LeerReqId(obj)
            If id = 0 Then Return Nothing
            Dim p As Pendiente = Nothing
            _pendientes.TryGetValue(id, p)
            Return p
        End Function

        Private Sub Resolver(p As Pendiente, resultado As JObject)
            If p Is Nothing Then Return
            Dim quitado As Pendiente = Nothing
            _pendientes.TryRemove(p.ReqId, quitado)
            p.Tcs.TrySetResult(resultado)
        End Sub

        ''' <summary>Envia una peticion correlacionada y espera su respuesta (o timeout).</summary>
        Private Async Function EnviarYEsperarAsync(p As Pendiente, req As JObject, timeoutMs As Integer) As Task(Of JObject)
            req("req_id") = p.ReqId
            Try
                Await EnviarAsync(req)
            Catch ex As Exception
                Resolver(p, Nothing)
                Return New JObject From {{"ok", False}, {"error", "No se pudo enviar a Deriv: " & ex.Message}}
            End Try
            Dim t = p.Tcs.Task
            If Await Task.WhenAny(t, Task.Delay(timeoutMs)) Is t AndAlso t.Result IsNot Nothing Then
                Return t.Result
            End If
            Return New JObject From {{"ok", False}, {"error", "Timeout: Deriv no respondio a tiempo."}, {"req_id", p.ReqId}}
        End Function
#End Region

#Region "ITradingService"
        Public Function GetStatus() As JObject Implements ITradingService.GetStatus
            Return New JObject From {
                {"ok", True},
                {"connected", EstaConectado},
                {"demo", _esDemo},
                {"cuenta", If(_esDemo, "DEMO", "REAL")},
                {"balance", TextoSaldo},
                {"account", If(_cliente?.Name, "")},
                {"timestamp", DateTime.UtcNow.ToString("o")}
            }
        End Function

        Public Function GetOperations() As JArray Implements ITradingService.GetOperations
            Dim arr As New JArray()
            For Each op In ObtenerOperaciones()
                arr.Add(New JObject From {
                    {"id", op.Id}, {"contract_id", op.ContractId}, {"tipo", op.Tipo},
                    {"simbolo", op.Simbolo}, {"monto", op.Monto}, {"estado", op.Estado},
                    {"profit_loss", op.ProfitLoss}, {"hora", op.Hora}, {"entry_spot", op.EntrySpot},
                    {"spot_actual", op.SpotActual}, {"tiempo_restante", op.TiempoRestante},
                    {"estrategia", op.Estrategia}
                })
            Next
            Return arr
        End Function

        ''' <summary>
        ''' Compra AUTOMATICA (estrategias, API, SignalR). Pasa por el gestor de riesgo.
        ''' body: contract_type, symbol, amount, duration, duration_unit, [basis, currency,
        ''' barrier, barrier2, _meta_estrategia, _meta_sesion_id, _meta_op_id].
        ''' </summary>
        Public Async Function ExecuteTradeAsync(body As JObject) As Task(Of JObject) Implements ITradingService.ExecuteTradeAsync
            If Not EstaConectado Then
                Return New JObject From {{"ok", False}, {"error", "Trading no conectado. Conecta una cuenta primero."}}
            End If
            Dim contractType = body("contract_type")?.ToString()
            Dim symbol = body("symbol")?.ToString()
            Dim amount As Double = If(body("amount") IsNot Nothing, CDbl(body("amount")), 0)
            If String.IsNullOrEmpty(contractType) OrElse String.IsNullOrEmpty(symbol) OrElse amount <= 0 Then
                Return New JObject From {{"ok", False}, {"error", "Requeridos: contract_type, symbol, amount > 0"}}
            End If

            Dim motivo = RiskManager.ValidarYReservar(CDec(amount), _esDemo)
            If motivo <> "" Then
                Return New JObject From {{"ok", False}, {"error", "Riesgo: " & motivo}, {"bloqueado_por_riesgo", True}}
            End If
            Try
                Dim p = NuevoPendiente(TipoPendiente.CompraAutomatica, CDec(amount), body)
                Dim res = Await EnviarYEsperarAsync(p, ArmarPropuesta(body, contractType, symbol, amount), 10000)
                If res("ok") IsNot Nothing AndAlso Not CBool(res("ok")) AndAlso res("req_id") IsNot Nothing Then
                    ' Timeout: el pendiente se conserva para que un "buy" tardio quede etiquetado.
                    AppLogger.Warn("[Trading] Timeout esperando compra req_id=" & p.ReqId & " (" & contractType & " " & symbol & ")")
                    _pendientes(p.ReqId) = p
                End If
                Return res
            Finally
                RiskManager.LiberarReserva()
            End Try
        End Function

        Public Async Function ExecuteSellAsync(contractId As String) As Task(Of JObject) Implements ITradingService.ExecuteSellAsync
            If Not EstaConectado Then Return New JObject From {{"ok", False}, {"error", "Trading no conectado"}}
            Dim p = NuevoPendiente(TipoPendiente.Venta)
            Return Await EnviarYEsperarAsync(p, New JObject From {{"sell", IdContrato(contractId)}, {"price", 0}}, 10000)
        End Function

        ''' <summary>
        ''' Conexion sin interfaz (modo agente / API). Busca el cliente en BD por nombre
        ''' (o el primero con credenciales) y conecta la cuenta demo o real.
        ''' </summary>
        Public Async Function ConectarTradingAsync(esDemo As Boolean, Optional nombreCliente As String = "") As Task(Of JObject) _
            Implements ITradingService.ConectarTradingAsync
            If EstaConectado AndAlso _esDemo = esDemo AndAlso
               (String.IsNullOrWhiteSpace(nombreCliente) OrElse
                If(_cliente?.Name, "").IndexOf(nombreCliente, StringComparison.OrdinalIgnoreCase) >= 0) Then
                Return New JObject From {{"ok", True}, {"mensaje", "Ya conectado."}, {"estado", GetStatus()}}
            End If
            Try
                Dim repo As New ClientRepository()
                Dim clientes = Await repo.GetAllAsync()
                Dim candidatos = If(String.IsNullOrWhiteSpace(nombreCliente), clientes,
                                    clientes.Where(Function(c) c.Name.IndexOf(nombreCliente, StringComparison.OrdinalIgnoreCase) >= 0).ToList())
                For Each c In candidatos
                    Dim creds = Await repo.GetDerivCredentialsAsync(c.Id)
                    Dim token = If(esDemo, creds?.ApiTokenVirtual, creds?.ApiTokenReal)
                    If String.IsNullOrWhiteSpace(token) Then Continue For
                    Dim err = Await ConectarAsync(c, creds, esDemo)
                    Return New JObject From {
                        {"ok", err = ""}, {"connected", EstaConectado}, {"demo", esDemo},
                        {"cliente", c.Name}, {"error", err}
                    }
                Next
                Return New JObject From {{"ok", False}, {"error", If(String.IsNullOrWhiteSpace(nombreCliente),
                    "Ningun cliente tiene token " & If(esDemo, "demo", "real") & " configurado.",
                    "Cliente '" & nombreCliente & "' no encontrado o sin token.")}}
            Catch ex As Exception
                Return New JObject From {{"ok", False}, {"error", ex.Message}}
            End Try
        End Function

        Private Shared Function ArmarPropuesta(body As JObject, contractType As String, symbol As String, amount As Double) As JObject
            Dim req As New JObject From {
                {"proposal", 1},
                {"amount", amount},
                {"basis", If(body("basis")?.ToString(), "stake")},
                {"contract_type", contractType},
                {"currency", If(body("currency")?.ToString(), "USD")},
                {"duration", If(body("duration") IsNot Nothing, CInt(body("duration")), 5)},
                {"duration_unit", If(body("duration_unit")?.ToString(), "t")},
                {"underlying_symbol", symbol}
            }
            If body("barrier") IsNot Nothing Then req("barrier") = body("barrier").ToString()
            If body("barrier2") IsNot Nothing Then req("barrier2") = body("barrier2").ToString()
            Return req
        End Function
#End Region

#Region "Operaciones manuales (UI)"
        ''' <summary>
        ''' Pide una cotizacion (proposal) y la devuelve: {ok, id, ask_price, payout, longcode}.
        ''' No compra. Las operaciones manuales no pasan por el gestor de riesgo (son decision
        ''' del usuario) pero su resultado si cuenta para la perdida diaria.
        ''' </summary>
        Public Async Function CotizarAsync(contractType As String, symbol As String, monto As Double,
                                           duracion As Integer, unidad As String,
                                           Optional basis As String = "stake", Optional moneda As String = "USD",
                                           Optional barrera As String = Nothing, Optional barrera2 As String = Nothing) As Task(Of JObject)
            If Not EstaConectado Then Return New JObject From {{"ok", False}, {"error", "No hay conexion activa."}}
            Dim body As New JObject From {{"basis", basis}, {"currency", moneda}, {"duration", duracion}, {"duration_unit", unidad}}
            If Not String.IsNullOrEmpty(barrera) Then body("barrier") = barrera
            If Not String.IsNullOrEmpty(barrera2) Then body("barrier2") = barrera2
            Dim p = NuevoPendiente(TipoPendiente.Cotizacion, CDec(monto))
            Return Await EnviarYEsperarAsync(p, ArmarPropuesta(body, contractType, symbol, monto), 10000)
        End Function

        ''' <summary>
        ''' Cotiza un multiplicador (MULTUP / MULTDOWN) SIN comprar: sirve para medir la comision
        ''' y el stop out reales. Devuelve {ok, id, ask_price, proposal:{...}} o {ok:false, error}.
        ''' </summary>
        Public Async Function CotizarMultiplicadorAsync(contractType As String, symbol As String, monto As Double,
                                                        multiplicador As Integer,
                                                        Optional takeProfit As Double = 0, Optional stopLoss As Double = 0) As Task(Of JObject)
            If Not EstaConectado Then Return New JObject From {{"ok", False}, {"error", "No hay conexion activa."}}
            Dim req As New JObject From {
                {"proposal", 1}, {"amount", monto}, {"basis", "stake"}, {"contract_type", contractType},
                {"currency", "USD"}, {"multiplier", multiplicador}, {"underlying_symbol", symbol}
            }
            If takeProfit > 0 OrElse stopLoss > 0 Then
                Dim limite As New JObject()
                If takeProfit > 0 Then limite("take_profit") = takeProfit
                If stopLoss > 0 Then limite("stop_loss") = stopLoss
                req("limit_order") = limite
            End If
            Dim p = NuevoPendiente(TipoPendiente.Cotizacion, CDec(monto))
            Return Await EnviarYEsperarAsync(p, req, 10000)
        End Function

        ''' <summary>Compra una cotizacion ya obtenida. Devuelve {ok, contract_id, ...}.</summary>
        Public Async Function ComprarCotizacionAsync(proposalId As String, precio As Double) As Task(Of JObject)
            If Not EstaConectado Then Return New JObject From {{"ok", False}, {"error", "No hay conexion activa."}}
            Dim p = NuevoPendiente(TipoPendiente.Compra, CDec(precio))
            Return Await EnviarYEsperarAsync(p, New JObject From {{"buy", proposalId}, {"price", precio}}, 10000)
        End Function

        ''' <summary>Cotiza y compra en un paso (botones simples CALL/PUT).</summary>
        Public Async Function OperarManualAsync(contractType As String, symbol As String, monto As Double,
                                                duracion As Integer, unidad As String) As Task(Of JObject)
            Dim cot = Await CotizarAsync(contractType, symbol, monto, duracion, unidad)
            If cot("ok") Is Nothing OrElse Not CBool(cot("ok")) Then Return cot
            Return Await ComprarCotizacionAsync(cot("id").ToString(), CDbl(cot("ask_price")))
        End Function

        ''' <summary>Vende todas las operaciones Abierto. Devuelve cuantas ordenes se enviaron.</summary>
        Public Async Function VenderTodasAbiertasAsync() As Task(Of Integer)
            Dim n = 0
            For Each op In ObtenerOperaciones().Where(Function(o) o.Estado = "Abierto" AndAlso Not String.IsNullOrEmpty(o.ContractId))
                Try
                    Await EnviarAsync(New JObject From {{"sell", IdContrato(op.ContractId)}, {"price", 0}})
                    n += 1
                    Await Task.Delay(150)
                Catch ex As Exception
                    EscribirLog(String.Format("Error vendiendo {0}: {1}", op.ContractId, ex.Message))
                End Try
            Next
            Return n
        End Function

        ''' <summary>Re-suscribe en Deriv las operaciones Abierto/Pendiente para obtener su estado real.</summary>
        Public Function SincronizarAbiertas() As Integer
            Dim abiertas = ObtenerOperaciones().Where(
                Function(o) Not String.IsNullOrEmpty(o.ContractId) AndAlso (o.Estado = "Abierto" OrElse o.Estado = "Pendiente")).ToList()
            For Each op In abiertas
                SuscribirContrato(op.ContractId)
            Next
            If abiertas.Count > 0 Then EscribirLog(String.Format("Re-suscripcion enviada para {0} contrato(s).", abiertas.Count))
            Return abiertas.Count
        End Function

        ''' <summary>Quita la operacion de la lista SIN tocar Deriv y la oculta en BD.</summary>
        Public Sub QuitarOperacion(contractId As String)
            Dim op = BuscarOp(contractId)
            If op Is Nothing Then Return
            SyncLock _opsLock
                _ops.Remove(op)
            End SyncLock
            RaiseEvent OperacionQuitada(op)
            Dim opIdLive = op.OperacionLiveId
            Dim opIdMan = op.Id
            Task.Run(Async Function() As Task
                         Try
                             Dim repoLive As New LiveTestRepository()
                             Await repoLive.OcultarOperacionPorContractIdAsync(contractId)
                             If opIdLive > 0 Then Await repoLive.OcultarOperacionPorIdAsync(opIdLive)
                             If opIdMan > 0 Then Await New ClientRepository().EliminarOperacionAsync(opIdMan)
                         Catch ex As Exception
                             AppLogger.Warn("[Trading] No se pudo ocultar " & contractId & ": " & ex.Message)
                         End Try
                     End Function)
        End Sub

        ''' <summary>Marca una operacion como de estrategia (callback OnTradeLiveAbierto de los motores).</summary>
        Public Sub TaggearOperacion(contractId As String, estrategia As String, sesionId As Integer, opId As Integer)
            Dim op = BuscarOp(contractId)
            If op Is Nothing Then Return
            op.Estrategia = estrategia
            op.EsLive = True
            op.SesionId = sesionId
            op.OperacionLiveId = opId
        End Sub

        ''' <summary>
        ''' contract_id como NUMERO en el JSON. Deriv rechaza el texto con
        ''' "Input validation failed: contract_id": por eso antes nunca llegaban los
        ''' cierres de contrato (suscripcion, reconciliacion y venta fallaban).
        ''' </summary>
        Private Shared Function IdContrato(contractId As String) As JToken
            Dim n As Long
            If Long.TryParse(contractId, n) Then Return New JValue(n)
            Return New JValue(contractId)
        End Function

        Public Sub SuscribirContrato(contractId As String)
            If String.IsNullOrEmpty(contractId) OrElse Not EstaConectado Then Return
            EnviarSinEsperar(New JObject From {{"proposal_open_contract", 1}, {"contract_id", IdContrato(contractId)}, {"subscribe", 1}})
        End Sub
#End Region

#Region "Recuperacion y reconciliacion"
        ''' <summary>
        ''' Carga las ultimas operaciones live de BD y se suscribe a las que siguen
        ''' Pendiente/Abierto para recibir su estado final.
        ''' </summary>
        Private Async Function RecuperarOperacionesLiveAsync() As Task
            Try
                Dim ops = Await New LiveTestRepository().GetUltimasOperacionesLiveAsync(100)
                If ops.Count = 0 Then
                    EscribirLog("Sin operaciones live previas por cargar.")
                    Return
                End If
                Dim suscritas = 0
                For Each p In ops
                    Dim creado As DateTime = DateTime.UtcNow
                    Try
                        creado = New DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(p.EpochEntrada)
                    Catch
                    End Try
                    Dim op As New OperacionModel With {
                        .ContractId = p.ContractId,
                        .Hora = creado.ToLocalTime().ToString("HH:mm:ss"),
                        .CreatedAt = creado,
                        .Tipo = p.Tipo,
                        .Simbolo = p.Symbol,
                        .Monto = p.Monto,
                        .Estado = p.Estado,
                        .ProfitLoss = p.ProfitLoss,
                        .TiempoRestante = If(p.Estado = "Pendiente" OrElse p.Estado = "Abierto", "?", "—"),
                        .Estrategia = p.Estrategia,
                        .EsLive = True,
                        .SesionId = p.SesionId,
                        .OperacionLiveId = p.OperacionId
                    }
                    Dim agregar As Boolean
                    SyncLock _opsLock
                        agregar = String.IsNullOrEmpty(p.ContractId) OrElse Not _ops.Any(Function(o) o.ContractId = p.ContractId)
                        If agregar Then _ops.Add(op)
                    End SyncLock
                    If agregar Then RaiseEvent OperacionAgregada(op)
                    If (p.Estado = "Pendiente" OrElse p.Estado = "Abierto") AndAlso Not String.IsNullOrEmpty(p.ContractId) Then
                        SuscribirContrato(p.ContractId)
                        suscritas += 1
                    End If
                Next
                EscribirLog(String.Format("Cargadas {0} ops live (de las cuales {1} aun pendientes/abiertas).", ops.Count, suscritas))
            Catch ex As Exception
                EscribirLog("Error cargando historial live: " & ex.Message)
            End Try
        End Function

        ''' <summary>Consulta a Deriv las filas 'Abierto' de trading_operations (sin suscripcion).</summary>
        Private Async Function ReconciliarOperacionesAbiertasAsync() As Task
            Try
                Dim ids = Await New ClientRepository().ObtenerContratosAbiertosAsync(500)
                If ids.Count = 0 Then Return
                EscribirLog(String.Format("Reconciliando {0} operaciones 'Abierto' de trading_operations con Deriv...", ids.Count))
                For Each cid In ids
                    If Not EstaConectado Then Exit For
                    ' Deriv limita proposal_open_contract: ~1 por segundo y, si avisa RateLimit, esperar.
                    Do While DateTime.UtcNow < _rateLimitHastaUtc AndAlso EstaConectado
                        Await Task.Delay(1000)
                    Loop
                    Await EnviarAsync(New JObject From {{"proposal_open_contract", 1}, {"contract_id", IdContrato(cid)}})
                    Await Task.Delay(1000)
                Next
                EscribirLog("Reconciliacion enviada. Las filas que Deriv no pudo responder se reintentan en la proxima conexion.")
            Catch ex As Exception
                AppLogger.Warn("[Trading] Reconciliacion de operaciones abiertas: " & ex.Message)
            End Try
        End Function
#End Region

#Region "Procesamiento de mensajes de Deriv"
        Private Sub ProcesarMensaje(json As String)
            Dim obj = JObject.Parse(json)
            Dim msgType = obj("msg_type")?.ToString()
            If String.IsNullOrEmpty(msgType) AndAlso obj("error") IsNot Nothing Then
                msgType = obj("error")?("msg_type")?.ToString()
            End If

            If obj("error") IsNot Nothing Then
                ProcesarError(obj)
                Return
            End If

            ' Respuesta a una consulta de solo lectura (ticks_history, active_symbols...)
            Dim consulta = BuscarPendiente(obj)
            If consulta IsNot Nothing AndAlso consulta.Tipo = TipoPendiente.Consulta Then
                obj("ok") = True
                Resolver(consulta, obj)
                Return
            End If

            ' Tick de una suscripcion de velas en vivo
            If msgType = "ohlc" Then
                Dim subId = obj("subscription")?("id")?.ToString()
                Dim handler As Action(Of JObject) = Nothing
                If subId IsNot Nothing AndAlso _suscripcionesVelas.TryGetValue(subId, handler) Then
                    Try
                        handler(CType(obj("ohlc"), JObject))
                    Catch ex As Exception
                        AppLogger.Warn("[Trading] Error en suscriptor de velas: " & ex.Message)
                    End Try
                End If
                Return
            End If

            Select Case msgType
                Case "pong"
                    ' respuesta al ping
                Case "balance" : ProcesarBalance(obj)
                Case "proposal" : ProcesarPropuesta(obj)
                Case "buy" : ProcesarCompra(obj)
                Case "proposal_open_contract" : ProcesarContrato(obj)
                Case "sell" : ProcesarVenta(obj)
            End Select
        End Sub

        Private Sub ProcesarError(obj As JObject)
            Dim errMsg = obj("error")?("message")?.ToString()
            Dim errCode = obj("error")?("code")?.ToString()
            Dim errDetails = obj("error")?("details")?.ToString()

            If errCode = "ContractNotFound" Then
                ' El contrato ya no existe en Deriv: quitarlo de la lista y marcar BD.
                Dim cid = ContractIdDeEcho(obj("echo_req"))
                If Not String.IsNullOrEmpty(cid) Then
                    Dim op = BuscarOp(cid)
                    If op IsNot Nothing Then
                        SyncLock _opsLock
                            _ops.Remove(op)
                        End SyncLock
                        RaiseEvent OperacionQuitada(op)
                    End If
                    Dim esLive = op IsNot Nothing AndAlso op.EsLive
                    Task.Run(Async Function() As Task
                                 Try
                                     If esLive Then Await New LiveTestRepository().MarcarOperacionExpiradaPorContractIdAsync(cid)
                                     Await New ClientRepository().CerrarOperacionPorContractAsync(cid, "NoEncontrado", 0D)
                                 Catch ex As Exception
                                     AppLogger.Warn("[Trading] No se pudo marcar " & cid & " como no encontrado: " & ex.Message)
                                 End Try
                             End Function)
                End If
                EscribirLog(String.Format("[Deriv] {0} (contrato {1} limpiado)", errMsg, If(cid, "?")))
                Resolver(BuscarPendiente(obj), New JObject From {{"ok", False}, {"error", errMsg}})
                Return
            End If

            If errCode = "RateLimit" Then
                ' Pausar la reconciliacion (y no ensuciar la UI) hasta que Deriv libere el limite
                _rateLimitHastaUtc = DateTime.UtcNow.AddSeconds(30)
                Resolver(BuscarPendiente(obj), New JObject From {{"ok", False}, {"error", errMsg & " [RateLimit]"}})
                Return
            End If

            Dim fullErr = errMsg & If(String.IsNullOrEmpty(errCode), "", " [" & errCode & "]") &
                          If(String.IsNullOrEmpty(errDetails), "", " — " & errDetails)
            ' Todo error de Deriv queda en el log (antes solo se mostraba en la UI)
            If Interlocked.Increment(_erroresLogueados) <= 20 Then
                EscribirLog("[Deriv] error en " & If(obj("msg_type")?.ToString(), obj("error")?("msg_type")?.ToString()) &
                            ": " & fullErr & " | echo_req: " & Left(If(obj("echo_req")?.ToString(Newtonsoft.Json.Formatting.None), ""), 200))
            End If
            Avisar(fullErr, esError:=True)
            Resolver(BuscarPendiente(obj), New JObject From {{"ok", False}, {"error", fullErr}})
        End Sub

        ''' <summary>contract_id de echo_req segun el tipo de peticion (sell / proposal_open_contract).</summary>
        Private Shared Function ContractIdDeEcho(eco As JToken) As String
            If eco Is Nothing Then Return Nothing
            Dim sellTok = eco("sell")
            If sellTok IsNot Nothing AndAlso (sellTok.Type = JTokenType.String OrElse sellTok.Type = JTokenType.Integer) Then Return sellTok.ToString()
            If eco("contract_id") IsNot Nothing Then Return eco("contract_id").ToString()
            Dim poc = eco("proposal_open_contract")?.ToString()
            If Not String.IsNullOrEmpty(poc) AndAlso poc <> "1" AndAlso poc <> "True" Then Return poc
            Return Nothing
        End Function

        Private Sub ProcesarBalance(obj As JObject)
            Dim bal = obj("balance")
            If bal Is Nothing Then Return
            _saldo = CDec(bal("balance"))
            _moneda = If(bal("currency")?.ToString(), "USD")
            RaiseEvent SaldoActualizado(_saldo.Value, _moneda)
        End Sub

        Private Sub ProcesarPropuesta(obj As JObject)
            Dim prop = obj("proposal")
            If prop Is Nothing Then Return
            Dim p = BuscarPendiente(obj)
            If p Is Nothing Then Return   ' cotizacion sin dueno (p.ej. de una sesion anterior)
            Dim proposalId = prop("id")?.ToString()
            Dim ask As Double = If(prop("ask_price") IsNot Nothing, CDbl(prop("ask_price")), 0)
            Dim payout As Double = If(prop("payout") IsNot Nothing, CDbl(prop("payout")), 0)

            Select Case p.Tipo
                Case TipoPendiente.Cotizacion
                    Resolver(p, New JObject From {
                        {"ok", True}, {"id", proposalId}, {"ask_price", ask}, {"payout", payout},
                        {"longcode", If(prop("longcode")?.ToString(), "")},
                        {"proposal", prop.DeepClone()}
                    })
                Case TipoPendiente.CompraAutomatica
                    ' basis=stake: Deriv debe cobrar exactamente el monto pedido
                    If CDec(ask) > p.Monto + 0.01D Then
                        Resolver(p, New JObject From {{"ok", False}, {"error",
                            String.Format(Globalization.CultureInfo.InvariantCulture,
                                "Precio cotizado {0:F2} mayor al monto pedido {1:F2}: compra cancelada.", ask, p.Monto)}})
                        Return
                    End If
                    ' mismo req_id: el "buy" vuelve a este mismo pendiente
                    EnviarSinEsperar(New JObject From {{"buy", proposalId}, {"price", ask}, {"req_id", p.ReqId}})
            End Select
        End Sub

        Private Sub ProcesarCompra(obj As JObject)
            Dim buy = obj("buy")
            If buy Is Nothing Then Return
            Dim op As New OperacionModel()
            op.ContractId = buy("contract_id")?.ToString()
            Dim shortcode = If(buy("shortcode")?.ToString(), "")
            op.Tipo = If(shortcode.StartsWith("CALL"), "CALL", If(shortcode.StartsWith("PUT"), "PUT", "?"))
            Dim parts = shortcode.Split("_"c)
            op.Simbolo = If(parts.Length >= 3, parts(1) & "_" & parts(2), shortcode)
            op.Monto = If(buy("buy_price") IsNot Nothing, CDec(buy("buy_price")), 0)
            op.Estado = "Abierto"
            op.ProfitLoss = 0
            op.TiempoRestante = "..."
            op.Hora = DateTime.Now.ToString("HH:mm:ss")
            op.Duracion = ""
            If _cliente IsNot Nothing Then op.ClientId = _cliente.Id

            ' Compra automatica: la metadata de estrategia viaja en su pendiente (req_id)
            Dim p = BuscarPendiente(obj)
            If p IsNot Nothing AndAlso Not String.IsNullOrEmpty(p.Estrategia) Then
                op.EsLive = True
                op.Estrategia = p.Estrategia
                op.SesionId = p.SesionId
                op.OperacionLiveId = p.OperacionLiveId
            End If

            SyncLock _opsLock
                _ops.Insert(0, op)
            End SyncLock
            RaiseEvent OperacionAgregada(op)
            Avisar(String.Format("{0} abierto: {1} — ${2}", op.Tipo, op.ContractId, op.Monto))

            Dim opBd = op
            Task.Run(Async Function() As Task
                         Try
                             Await New ClientRepository().GuardarOperacionAsync(opBd)
                         Catch ex As Exception
                             AppLogger.Error("[Trading] No se pudo guardar " & opBd.ContractId, ex)
                         End Try
                     End Function)
            RiskManager.RegistrarApertura(op.ContractId, op.Monto)
            SuscribirContrato(op.ContractId)

            Resolver(p, New JObject From {
                {"ok", True}, {"contract_id", op.ContractId}, {"tipo", op.Tipo}, {"simbolo", op.Simbolo},
                {"monto", op.Monto}, {"estado", op.Estado}, {"hora", op.Hora}
            })
        End Sub

        Private Sub ProcesarContrato(obj As JObject)
            Dim poc = obj("proposal_open_contract")
            If poc Is Nothing Then Return
            Dim contractId = poc("contract_id")?.ToString()
            If String.IsNullOrEmpty(contractId) Then
                ' Respuesta sin contrato (diagnostico de reconciliacion)
                Dim pedido = obj("echo_req")?("contract_id")?.ToString()
                If Interlocked.Increment(_pocVaciosLogueados) <= 3 Then
                    EscribirLog("POC sin contract_id para " & If(pedido, "?") & ": " &
                                Left(obj.ToString(Newtonsoft.Json.Formatting.None), 400))
                End If
                Return
            End If
            Dim status = poc("status")?.ToString()
            Dim profit As Decimal = If(poc("profit") IsNot Nothing, CDec(poc("profit")), 0)
            Dim vendido = poc("is_sold") IsNot Nothing AndAlso poc("is_sold").ToString() = "1"
            Dim cerrado = status = "won" OrElse status = "lost" OrElse status = "sold" OrElse vendido

            If cerrado Then
                ' Momento real del cierre (los cierres de otros dias no cuentan para hoy)
                Dim cierreEpoch As Long = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                If poc("sell_time") IsNot Nothing Then
                    Long.TryParse(poc("sell_time").ToString(), cierreEpoch)
                ElseIf poc("exit_tick_time") IsNot Nothing Then
                    Long.TryParse(poc("exit_tick_time").ToString(), cierreEpoch)
                End If
                RiskManager.RegistrarCierre(contractId, profit, cierreEpoch)
                Dim estadoReal = If(status = "won", "Ganado", If(status = "lost", "Perdido", If(profit > 0D, "Ganado", "Perdido")))
                Task.Run(Async Function() As Task
                             Try
                                 Await New ClientRepository().CerrarOperacionPorContractAsync(contractId, estadoReal, profit)
                             Catch ex As Exception
                                 AppLogger.Warn("[Trading] No se pudo cerrar " & contractId & " en trading_operations: " & ex.Message)
                             End Try
                         End Function)
            End If

            Dim op = BuscarOp(contractId)
            If op Is Nothing Then Return
            Dim estadoPrevio = op.Estado
            op.Estado = If(status = "won", "Ganado", If(status = "lost", "Perdido", "Abierto"))
            op.ProfitLoss = profit
            If poc("entry_tick") IsNot Nothing Then op.EntrySpot = poc("entry_tick").ToString()
            If poc("current_spot") IsNot Nothing Then op.SpotActual = poc("current_spot").ToString()
            Dim tickCount = If(poc("tick_count") IsNot Nothing, CInt(poc("tick_count")), 0)
            Dim ticksPassed = If(poc("tick_passed") IsNot Nothing, CInt(poc("tick_passed")), 0)
            If tickCount > 0 Then
                Dim ticksLeft = tickCount - ticksPassed
                op.TiempoRestante = If(ticksLeft > 0, ticksLeft & "t", "0t")
            ElseIf poc("date_expiry") IsNot Nothing Then
                Dim exp = DateTimeOffset.FromUnixTimeSeconds(CLng(poc("date_expiry"))).UtcDateTime
                Dim secs = CInt((exp - DateTime.UtcNow).TotalSeconds)
                op.TiempoRestante = If(secs > 0, secs & "s", "0s")
            End If

            If (status = "won" OrElse status = "lost") AndAlso estadoPrevio <> op.Estado Then
                CerrarOperacionLive(op, poc, status = "won", profit)
                RaiseEvent OperacionCerrada(op)
            End If
        End Sub

        ''' <summary>Cierre de una operacion de estrategia: BD live, motor y notificacion.</summary>
        Private Sub CerrarOperacionLive(op As OperacionModel, poc As JToken, ganada As Boolean, profit As Decimal)
            If Not op.EsLive OrElse op.OperacionLiveId <= 0 Then Return
            Dim spotSalida As Decimal = 0D
            Dim epochSalida As Long = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            If poc("exit_tick") IsNot Nothing Then
                Decimal.TryParse(poc("exit_tick").ToString(), Globalization.NumberStyles.Any,
                                 Globalization.CultureInfo.InvariantCulture, spotSalida)
            End If
            If poc("exit_tick_time") IsNot Nothing Then epochSalida = CLng(poc("exit_tick_time"))

            Dim opId = op.OperacionLiveId
            Dim estado = op.Estado
            Task.Run(Async Function() As Task
                         Try
                             Await New LiveTestRepository().CerrarOperacionConResultadoAsync(opId, estado, profit, spotSalida, epochSalida)
                         Catch ex As Exception
                             AppLogger.Warn("[Trading] Error cerrando op live: " & ex.Message)
                         End Try
                     End Function)

            ' Motor: los del Servidor Live y, si no esta ahi, los de la pestana Estrategias
            If op.SesionId > 0 Then
                Dim motor As LiveTestMotor = Nothing
                LiveStrategiesController.GetInstance().Motores.TryGetValue(op.SesionId, motor)
                If motor Is Nothing AndAlso BuscarMotorExterno IsNot Nothing Then
                    Try
                        motor = BuscarMotorExterno(op.SesionId)
                    Catch
                    End Try
                End If
                If motor IsNot Nothing Then
                    Try
                        motor.NotificarOpResueltaExterna(op.ContractId, ganada, profit)
                    Catch ex As Exception
                        AppLogger.Warn("[Trading] Error notificando al motor: " & ex.Message)
                    End Try
                End If
            End If

            EnviarNotificacionOpCerrada(op, epochSalida)
        End Sub

        Private Sub ProcesarVenta(obj As JObject)
            Dim sell = obj("sell")
            If sell Is Nothing Then Return
            Dim contractId = sell("contract_id")?.ToString()
            Dim soldFor = If(sell("sold_for") IsNot Nothing, CDec(sell("sold_for")), 0D)
            Dim op = BuscarOp(contractId)
            If op IsNot Nothing Then
                op.Estado = "Cerrado"
                op.ProfitLoss = soldFor - op.Monto
                op.TiempoRestante = "—"
            End If
            Avisar(String.Format("Contrato cerrado. Recibido: {0:F2}", soldFor))
            Resolver(BuscarPendiente(obj), New JObject From {
                {"ok", True}, {"contract_id", IdContrato(contractId)}, {"sold_for", soldFor},
                {"profit_loss", If(op IsNot Nothing, op.ProfitLoss, 0D)}
            })
        End Sub
#End Region

#Region "Notificacion WhatsApp"
        ''' <summary>Resumen de una operacion LIVE cerrada al cliente (en segundo plano).</summary>
        Private Sub EnviarNotificacionOpCerrada(op As OperacionModel, epochSalida As Long)
            If op Is Nothing OrElse Not op.EsLive OrElse _cliente Is Nothing OrElse _cliente.Id <= 0 Then Return
            Dim clientId = _cliente.Id
            Dim saldoTxt = TextoSaldo
            Dim cuentaTxt = If(_esDemo, "DEMO", "REAL")
            Dim preferirDemo = _esDemo
            Dim icono = If(op.Estado = "Ganado", "✅", If(op.Estado = "Perdido", "❌", "⚠"))
            Dim apertura = ""
            Try
                apertura = op.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss")
            Catch
            End Try
            Dim cierre = If(epochSalida > 0,
                            DateTimeOffset.FromUnixTimeSeconds(epochSalida).LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss"),
                            DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"))
            Dim estrategia = If(String.IsNullOrEmpty(op.Estrategia), "Manual", op.Estrategia)
            Dim tipo = If(op.Tipo, "") : Dim simbolo = If(op.Simbolo, "")
            Dim contractId = If(op.ContractId, "") : Dim estado = If(op.Estado, "")
            Dim monto = op.Monto : Dim pnl = op.ProfitLoss

            Task.Run(Async Function() As Task
                         Try
                             Dim pnlHoy As Decimal = 0D, pnlSem As Decimal = 0D, pnlMes As Decimal = 0D
                             Try
                                 Dim r = Await New LiveTestRepository().GetPnlPeriodosAsync()
                                 pnlHoy = r.Hoy : pnlSem = r.Semana : pnlMes = r.Mes
                             Catch
                             End Try
                             Dim sb As New StringBuilder()
                             sb.AppendLine(String.Format("{0} OPERACION CERRADA — {1}", icono, estrategia))
                             sb.AppendLine(String.Format("├ Estado: {0} {1}", icono, estado))
                             sb.AppendLine(String.Format("├ Tipo: {0}", tipo))
                             sb.AppendLine(String.Format("├ Activo: {0}", simbolo))
                             sb.AppendLine(String.Format("├ Monto: ${0:F2}", monto))
                             sb.AppendLine(String.Format("├ Resultado: {0:+0.00;-0.00;0.00}", pnl))
                             sb.AppendLine(String.Format("├ Apertura: {0}", apertura))
                             sb.AppendLine(String.Format("├ Cierre: {0}", cierre))
                             sb.AppendLine(String.Format("├ Contract ID: {0}", contractId))
                             sb.AppendLine(String.Format("├ Cuenta: {0}", cuentaTxt))
                             sb.AppendLine(String.Format("├ Saldo actual: {0}", saldoTxt))
                             sb.AppendLine(String.Format("├ Ganancia HOY: {0:+$0.00;-$0.00;$0.00}", pnlHoy))
                             sb.AppendLine(String.Format("├ Ganancia SEMANA: {0:+$0.00;-$0.00;$0.00}", pnlSem))
                             sb.Append(String.Format("└ Ganancia MES: {0:+$0.00;-$0.00;$0.00}", pnlMes))
                             Dim resp = Await New NotificacionService().EnviarMensajeClienteAsync(clientId, sb.ToString(), preferirDemo)
                             If Not resp.Exito Then AppLogger.Warn("[Notif] " & resp.Mensaje)
                         Catch ex As Exception
                             AppLogger.Warn("[Notif] Error enviando: " & ex.Message)
                         End Try
                     End Function)
        End Sub
#End Region

    End Class

End Namespace

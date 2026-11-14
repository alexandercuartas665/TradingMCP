' ============================================================
' MainWindow.TradingEnvio.vb
'
' Envio al WebSocket de trading de Deriv y correlacion de compras.
'
' 1) Canal unico de envio: ClientWebSocket NO admite dos SendAsync
'    simultaneos (el segundo lanza InvalidOperationException y la
'    compra se pierde). Todo envio pasa por EnviarTradingBytesAsync,
'    serializado con un SemaphoreSlim.
'
' 2) Compras automaticas correlacionadas por req_id: cada compra
'    tiene su propio TaskCompletionSource y su metadata (estrategia,
'    sesion, operacion). Antes habia UNA sola variable compartida
'    (_pendingApiTrade / _proxTrade*), y dos estrategias comprando a
'    la vez se pisaban: una recibia el contract_id de la otra.
'
' 3) Reconciliacion: al conectar se consulta a Deriv el estado de
'    los contratos que trading_operations tiene como 'Abierto'.
' ============================================================
Imports System.Collections.Concurrent
Imports System.Net.WebSockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB

    Partial Public Class MainWindow

        ' ── 1) Canal unico de envio ─────────────────────────────

        Private ReadOnly _envioTradingLock As New SemaphoreSlim(1, 1)

        ''' <summary>Envia al WS de trading, de a un mensaje por vez. Lanza si el socket no esta abierto.</summary>
        Private Async Function EnviarTradingBytesAsync(bytes As Byte()) As Task
            Dim ws = _tradingWs
            If ws Is Nothing OrElse ws.State <> WebSocketState.Open Then
                Throw New InvalidOperationException("WebSocket de trading no conectado.")
            End If
            Await _envioTradingLock.WaitAsync()
            Try
                Await ws.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)
            Finally
                _envioTradingLock.Release()
            End Try
        End Function

        ''' <summary>Para handlers sincronos: envia sin esperar y registra cualquier error.</summary>
        Private Sub EnviarTradingSinEsperar(bytes As Byte())
            Dim t = EnviarTradingBytesAsync(bytes)
            t.ContinueWith(Sub(x)
                               AppLogger.Error("[Trading] Envio al WebSocket fallo", x.Exception?.GetBaseException())
                           End Sub, TaskContinuationOptions.OnlyOnFaulted)
        End Sub

        ' ── 2) Compras correlacionadas por req_id ───────────────

        Private Class CompraPendiente
            Public Property ReqId As Integer
            Public Property Tcs As New TaskCompletionSource(Of JObject)()
            Public Property Monto As Decimal
            Public Property Estrategia As String = ""
            Public Property SesionId As Integer
            Public Property OperacionLiveId As Integer
            Public Property CreadaUtc As DateTime = DateTime.UtcNow
        End Class

        Private ReadOnly _comprasPendientes As New ConcurrentDictionary(Of Integer, CompraPendiente)()
        ' Deriv acepta req_id entero; arrancamos alto para no chocar con otros usos.
        Private _siguienteReqId As Integer = 100000

        Private Function NuevaCompraPendiente(monto As Decimal, body As JObject) As CompraPendiente
            ' Limpiar compras que nunca recibieron respuesta (mas de 2 minutos)
            Dim limite = DateTime.UtcNow.AddMinutes(-2)
            For Each kv In _comprasPendientes
                If kv.Value.CreadaUtc < limite Then
                    Dim viejo As CompraPendiente = Nothing
                    If _comprasPendientes.TryRemove(kv.Key, viejo) Then viejo.Tcs.TrySetResult(Nothing)
                End If
            Next
            Dim c As New CompraPendiente With {
                .ReqId = Interlocked.Increment(_siguienteReqId),
                .Monto = monto,
                .Estrategia = If(body("_meta_estrategia")?.ToString(), ""),
                .SesionId = If(body("_meta_sesion_id") IsNot Nothing, CInt(body("_meta_sesion_id")), 0),
                .OperacionLiveId = If(body("_meta_op_id") IsNot Nothing, CInt(body("_meta_op_id")), 0)
            }
            _comprasPendientes(c.ReqId) = c
            Return c
        End Function

        ''' <summary>req_id de la respuesta de Deriv (top-level o en echo_req). 0 si no trae.</summary>
        Private Shared Function LeerReqId(obj As JObject) As Integer
            Dim tok = If(obj("req_id"), obj("echo_req")?("req_id"))
            Dim v As Integer
            If tok IsNot Nothing AndAlso Integer.TryParse(tok.ToString(), v) Then Return v
            Return 0
        End Function

        Private Function BuscarCompraPendiente(obj As JObject) As CompraPendiente
            Dim reqId = LeerReqId(obj)
            If reqId = 0 Then Return Nothing
            Dim c As CompraPendiente = Nothing
            _comprasPendientes.TryGetValue(reqId, c)
            Return c
        End Function

        Private Sub ResolverCompraPendiente(c As CompraPendiente, resultado As JObject)
            If c Is Nothing Then Return
            Dim quitada As CompraPendiente = Nothing
            _comprasPendientes.TryRemove(c.ReqId, quitada)
            c.Tcs.TrySetResult(resultado)
        End Sub

        ''' <summary>
        ''' Llamado desde el handler "proposal" cuando la cotizacion corresponde a una
        ''' compra automatica: valida el precio y envia el "buy" con el mismo req_id.
        ''' </summary>
        Private Sub ComprarPropuestaPendiente(c As CompraPendiente, proposalId As String,
                                              askPrice As Double, payout As Double)
            ' basis=stake: Deriv debe cobrar exactamente el monto pedido.
            If CDec(askPrice) > c.Monto + 0.01D Then
                ResolverCompraPendiente(c, New JObject From {
                    {"ok", False},
                    {"error", String.Format(Globalization.CultureInfo.InvariantCulture,
                        "Precio cotizado {0:F2} mayor al monto pedido {1:F2}: compra cancelada.", askPrice, c.Monto)}})
                Return
            End If
            Dim buyReq As New JObject From {
                {"buy", proposalId},
                {"price", askPrice},
                {"req_id", c.ReqId}
            }
            EnviarTradingSinEsperar(Encoding.UTF8.GetBytes(buyReq.ToString()))
        End Sub

        ' ── 3) Reconciliacion con Deriv ─────────────────────────

        ''' <summary>
        ''' Consulta a Deriv (proposal_open_contract sin suscripcion) el estado de los
        ''' contratos que trading_operations tiene como 'Abierto'. Las respuestas las
        ''' procesa el handler normal, que cierra la fila con el resultado real.
        ''' </summary>
        Private Async Function ReconciliarOperacionesAbiertasAsync() As Task
            Try
                Dim ids = Await New ClientRepository().ObtenerContratosAbiertosAsync(500)
                If ids.Count = 0 Then Return
                LogConexion(String.Format("Reconciliando {0} operaciones 'Abierto' de trading_operations con Deriv...", ids.Count))
                For Each cid In ids
                    If _tradingWs Is Nothing OrElse _tradingWs.State <> WebSocketState.Open Then Exit For
                    Dim req As New JObject From {{"proposal_open_contract", 1}, {"contract_id", cid}}
                    Await EnviarTradingBytesAsync(Encoding.UTF8.GetBytes(req.ToString()))
                    Await Task.Delay(120)   ' no saturar el WS (limites de Deriv)
                Next
            Catch ex As Exception
                AppLogger.Warn("[Trading] Reconciliacion de operaciones abiertas: " & ex.Message)
            End Try
        End Function

    End Class

End Namespace

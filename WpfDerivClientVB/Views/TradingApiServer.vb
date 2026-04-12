' ============================================================
' TradingApiServer.vb
' Servidor HTTP embebido (HttpListener) en la app WPF.
' Permite controlar el trading via curl / HTTP.
'
' Endpoints disponibles:
'   GET  /api/status            — estado de conexion y balance
'   GET  /api/operations        — lista de operaciones de sesion
'   GET  /api/analizar          — analisis EMA+RSI del mercado
'                                 ?symbol=R_100&periodo=20&min_confianza=40
'   POST /api/analizar-y-operar — analiza y ejecuta si hay senal
'   POST /api/trade             — ejecutar operacion manualmente
'   POST /api/sell              — cerrar contrato abierto
'   GET  /api/help              — documentacion completa
' ============================================================
Imports System.Net
Imports System.Net.WebSockets
Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Indicators
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias

Namespace WpfDerivClientVB

    Public Class TradingApiServer

        Private _listener As HttpListener
        Private _cts As CancellationTokenSource
        Public ReadOnly Property Port As Integer
        Public ReadOnly Property IsRunning As Boolean
            Get
                Return _listener IsNot Nothing AndAlso _listener.IsListening
            End Get
        End Property

        ' Callbacks que MainWindow implementa
        Public Property GetStatus As Func(Of JObject)
        Public Property GetOperations As Func(Of JArray)
        Public Property ExecuteTrade As Func(Of JObject, Task(Of JObject))
        Public Property ExecuteSell As Func(Of String, Task(Of JObject))
        Public Property LogAction As Action(Of String)
        Public Property DescargaService As DescargaService

        Public Sub New(port As Integer)
            _Port = port
        End Sub

        Public Sub Start()
            If IsRunning Then Return
            _cts = New CancellationTokenSource()
            _listener = New HttpListener()
            _listener.Prefixes.Add(String.Format("http://localhost:{0}/", _Port))
            _listener.Start()
#Disable Warning BC42358
            Task.Run(Function() ListenLoop())
#Enable Warning BC42358
        End Sub

        Public Sub [Stop]()
            _cts?.Cancel()
            Try
                _listener?.Stop()
                _listener?.Close()
            Catch
            End Try
            _listener = Nothing
        End Sub

        ' ===================================================
        '  BUCLE PRINCIPAL
        ' ===================================================
        Private Async Function ListenLoop() As Task
            Do While _listener IsNot Nothing AndAlso Not _cts.IsCancellationRequested
                Dim debeEsperar As Boolean = False
                Try
                    If _listener Is Nothing OrElse Not _listener.IsListening Then Exit Do
                    Dim ctx = Await _listener.GetContextAsync()
#Disable Warning BC42358
                    Task.Run(Function() HandleRequest(ctx))
#Enable Warning BC42358
                Catch ex As ObjectDisposedException
                    Exit Do
                Catch ex As System.Net.HttpListenerException
                    If _cts.IsCancellationRequested Then Exit Do
                    If LogAction IsNot Nothing Then
                        LogAction.Invoke(String.Format("[Server] HttpListenerException: {0} — reintentando...", ex.Message))
                    End If
                    debeEsperar = True
                Catch ex As Exception
                    If _cts.IsCancellationRequested Then Exit Do
                    If LogAction IsNot Nothing Then
                        LogAction.Invoke(String.Format("[Server] Error en ListenLoop: {0}", ex.Message))
                    End If
                End Try
                If debeEsperar Then Await Task.Delay(500)
            Loop
        End Function

        ' ===================================================
        '  DISPATCHER DE RUTAS
        ' ===================================================
        Private Async Function HandleRequest(ctx As HttpListenerContext) As Task
            Dim req = ctx.Request
            Dim res = ctx.Response
            res.ContentType = "application/json; charset=utf-8"
            res.Headers.Add("Access-Control-Allow-Origin", "*")

            Dim path = req.Url.AbsolutePath.TrimEnd("/"c).ToLower()
            Dim method = req.HttpMethod.ToUpper()
            Dim qs = req.QueryString   ' query string params

            If LogAction IsNot Nothing Then
                LogAction.Invoke(String.Format("[{0}] {1} {2}", DateTime.Now.ToString("HH:mm:ss"), method, path))
            End If

            Dim respCode As Integer = 200
            Dim respBody As String = ""

            Try
                ' Leer body POST
                Dim body As JObject = Nothing
                If method = "POST" Then
                    Using sr As New StreamReader(req.InputStream, Encoding.UTF8)
                        Dim raw = Await sr.ReadToEndAsync()
                        If Not String.IsNullOrWhiteSpace(raw) Then body = JObject.Parse(raw)
                    End Using
                End If

                Dim result As JObject

                ' ── Operaciones (devuelve JArray) ──
                If path = "/api/operations" Then
                    Dim ops = If(GetOperations IsNot Nothing, GetOperations.Invoke(), New JArray())
                    If LogAction IsNot Nothing Then LogAction.Invoke(String.Format("  → OK ({0} ops)", ops.Count))
                    respCode = 200
                    respBody = ops.ToString(Formatting.Indented)
                    Await WriteResponse(res, respCode, respBody)
                    Return
                End If

                ' ── Catalogo de indicadores disponibles ──
                If path = "/api/indicadores" Then
                    Dim todos = IndicatorRegistry.GetAll()
                    Dim arr As New JArray()
                    For Each ind In todos
                        arr.Add(New JObject From {
                            {"nombre", ind.Name},
                            {"categoria", ind.Category},
                            {"descripcion", ind.Description}
                        })
                    Next
                    Dim cat As New JObject From {{"ok", True}, {"total", todos.Count}, {"indicadores", arr}}
                    respCode = 200
                    respBody = cat.ToString(Formatting.Indented)
                    Await WriteResponse(res, respCode, respBody)
                    Return
                End If

                ' ── Calcular cualquier indicador ──
                If path = "/api/indicador" Then
                    Dim symbol = If(qs("symbol"), "R_100")
                    Dim nombre = If(qs("nombre"), "RSI")
                    Dim count As Integer = 50
                    Integer.TryParse(If(qs("count"), "50"), count)
                    Dim granularity As Integer = 60
                    Integer.TryParse(If(qs("granularity"), "60"), granularity)
                    Dim ultimos As Integer = 10
                    Integer.TryParse(If(qs("ultimos"), "10"), ultimos)
                    result = Await EjecutarIndicadorAsync(symbol, nombre, count, granularity, ultimos)
                    If LogAction IsNot Nothing Then
                        LogAction.Invoke(String.Format("  -> Indicador {0} en {1}", nombre, symbol))
                    End If
                    respCode = 200
                    respBody = result.ToString(Formatting.Indented)
                    Await WriteResponse(res, respCode, respBody)
                    Return
                End If

                ' ── Análisis de mercado ──
                If path = "/api/analizar" Then
                    Dim symbol = If(qs("symbol"), "R_100")
                    Dim periodo As Integer = 20
                    Integer.TryParse(If(qs("periodo"), "20"), periodo)
                    Dim minConf As Integer = 40
                    Integer.TryParse(If(qs("min_confianza"), "40"), minConf)
                    result = Await AnalizarMercadoAsync(symbol, periodo)
                    If LogAction IsNot Nothing Then
                        LogAction.Invoke(String.Format("  → {0} score:{1} confianza:{2}",
                            result("senal"), result("score"), result("confianza")))
                    End If
                    respCode = 200
                    respBody = result.ToString(Formatting.Indented)
                    Await WriteResponse(res, respCode, respBody)
                    Return
                End If

                ' ── Seykota: Donchian + ATR ──
                If path = "/api/seykota" Then
                    Dim symbol = If(qs("symbol"), "R_100")
                    Dim periodo As Integer = 20
                    Integer.TryParse(If(qs("periodo"), "20"), periodo)
                    Dim granularity As Integer = 60
                    Integer.TryParse(If(qs("granularity"), "60"), granularity)
                    result = Await AnalizarSeykotaAsync(symbol, periodo, granularity)
                    If LogAction IsNot Nothing Then
                        LogAction.Invoke(String.Format("  -> Seykota {0} | {1} | conf:{2}% | DC:{3}-{4} | ATR:{5}",
                            symbol, result("senal"), result("confianza"),
                            result("dc_lower"), result("dc_upper"), result("atr14")))
                    End If
                    respCode = 200
                    respBody = result.ToString(Formatting.Indented)
                    Await WriteResponse(res, respCode, respBody)
                    Return
                End If

                ' ── Analizar y operar automáticamente ──
                If path = "/api/analizar-y-operar" Then
                    If method <> "POST" Then
                        result = ErrorJson("Use POST /api/analizar-y-operar")
                    ElseIf ExecuteTrade Is Nothing Then
                        result = ErrorJson("Trading no conectado. Conecta una cuenta primero.")
                    Else
                        Dim symbol = If(body?("symbol")?.ToString(), "R_100")
                        Dim periodo As Integer = 20
                        Integer.TryParse(If(body?("periodo")?.ToString(), "20"), periodo)
                        Dim minConf As Integer = 40
                        Integer.TryParse(If(body?("min_confianza")?.ToString(), "40"), minConf)
                        Dim amount As Double = If(body("amount") IsNot Nothing, CDbl(body("amount")), 1.0)
                        Dim duration As Integer = If(body("duration") IsNot Nothing, CInt(body("duration")), 5)
                        Dim durUnit As String = If(body?("duration_unit")?.ToString(), "t")

                        ' 1. Analizar
                        Dim analisis = Await AnalizarMercadoAsync(symbol, periodo)

                        ' Si el analisis fallo (timeout, simbolo invalido, etc.), devolver error directamente
                        If analisis("ok")?.ToObject(Of Boolean)() = False Then
                            result = analisis
                            respCode = 200
                            respBody = result.ToString(Formatting.Indented)
                            Await WriteResponse(res, respCode, respBody)
                            Return
                        End If

                        Dim senal = analisis("senal").ToString()
                        Dim confianza As Integer = CInt(analisis("confianza"))

                        If LogAction IsNot Nothing Then
                            LogAction.Invoke(String.Format("  → Analisis: {0} {1} pct confianza", senal, confianza))
                        End If

                        If senal = "NEUTRAL" Then
                            result = New JObject From {
                                {"ok", False},
                                {"motivo", "Senal NEUTRAL - mercado sin direccion clara"},
                                {"analisis", analisis}
                            }
                        ElseIf confianza < minConf Then
                            result = New JObject From {
                                {"ok", False},
                                {"motivo", String.Format("Confianza {0} pct menor al minimo requerido {1} pct", confianza, minConf)},
                                {"analisis", analisis}
                            }
                        Else
                            ' 2. Construir orden y ejecutar
                            Dim tradeBody As New JObject()
                            tradeBody.Add("contract_type", senal)
                            tradeBody.Add("symbol", symbol)
                            tradeBody.Add("amount", amount)
                            tradeBody.Add("basis", "stake")
                            tradeBody.Add("currency", "USD")
                            tradeBody.Add("duration", duration)
                            tradeBody.Add("duration_unit", durUnit)

                            Dim tradeResult = Await ExecuteTrade(tradeBody)

                            If LogAction IsNot Nothing Then
                                Dim okV = tradeResult("ok")?.ToString()
                                LogAction.Invoke(String.Format("  → Trade: {0}", If(okV = "True", "EJECUTADO", "FALLO")))
                            End If

                            result = New JObject From {
                                {"ok", tradeResult("ok")},
                                {"analisis", analisis},
                                {"operacion", tradeResult}
                            }
                        End If
                    End If

                    If LogAction IsNot Nothing Then
                        LogAction.Invoke(String.Format("  → {0}", If(result("ok")?.ToString() = "True", "OK", "NO operado")))
                    End If
                    respCode = 200
                    respBody = result.ToString(Formatting.Indented)
                    Await WriteResponse(res, respCode, respBody)
                    Return
                End If

                ' ── Rutas estándar ──
                Select Case path
                    Case "/api/status"
                        result = If(GetStatus IsNot Nothing, GetStatus.Invoke(), ErrorJson("No handler"))

                    Case "/api/trade"
                        If method <> "POST" Then
                            result = ErrorJson("Use POST /api/trade")
                        ElseIf body Is Nothing Then
                            result = ErrorJson("Body JSON requerido")
                        ElseIf ExecuteTrade Is Nothing Then
                            result = ErrorJson("Trading no conectado")
                        Else
                            result = Await ExecuteTrade(body)
                        End If

                    Case "/api/sell"
                        If method <> "POST" Then
                            result = ErrorJson("Use POST /api/sell")
                        ElseIf body Is Nothing OrElse body("contract_id") Is Nothing Then
                            result = ErrorJson("Falta contract_id en body")
                        ElseIf ExecuteSell Is Nothing Then
                            result = ErrorJson("Trading no conectado")
                        Else
                            result = Await ExecuteSell(body("contract_id").ToString())
                        End If

                    ' ── Historial: iniciar descarga ──────────────────────
                    Case "/api/historico/descargar"
                        If DescargaService Is Nothing Then
                            result = ErrorJson("DescargaService no inicializado")
                        ElseIf DescargaService.EstaDescargando Then
                            Dim est = DescargaService.ObtenerEstado()
                            result = New JObject From {
                                {"ok", False},
                                {"error", "Ya hay una descarga activa para " & est.Symbol & " " & est.Timeframe},
                                {"estado", EstadoComoJson(est)}
                            }
                        Else
                            Dim sym2 = qs("symbol")
                            Dim tf2 = qs("timeframe")
                            Dim desdeStr = qs("desde")
                            Dim hastaStr = qs("hasta")
                            If String.IsNullOrWhiteSpace(sym2) OrElse String.IsNullOrWhiteSpace(tf2) Then
                                result = ErrorJson("Parametros requeridos: symbol, timeframe, desde, hasta")
                            Else
                                Dim fromD, toD As DateTime
                                If Not DateTime.TryParse(desdeStr, fromD) Then fromD = DateTime.Today.AddDays(-30)
                                If Not DateTime.TryParse(hastaStr, toD) Then toD = DateTime.Today
                                ' Lanzar en background — no esperar para no bloquear HTTP
#Disable Warning BC42358
                                Task.Run(Async Function() As Task
                                             Try
                                                 Await DescargaService.DescargarAsync(sym2, tf2, fromD, toD)
                                             Catch
                                             End Try
                                         End Function)
#Enable Warning BC42358
                                result = New JObject From {
                                    {"ok", True},
                                    {"mensaje", String.Format("Descarga iniciada en background: {0} {1} {2}→{3}", sym2, tf2, fromD.ToString("yyyy-MM-dd"), toD.ToString("yyyy-MM-dd"))},
                                    {"estado_url", "/api/historico/estado"}
                                }
                            End If
                        End If

                    ' ── Historial: estado del job ─────────────────────────
                    Case "/api/historico/estado"
                        If DescargaService Is Nothing Then
                            result = ErrorJson("DescargaService no inicializado")
                        Else
                            result = EstadoComoJson(DescargaService.ObtenerEstado())
                        End If

                    ' ── Historial: consultar datos de BD ──────────────────
                    Case "/api/historico/datos"
                        If DescargaService Is Nothing Then
                            result = ErrorJson("DescargaService no inicializado")
                        Else
                            Dim symD = If(qs("symbol"), "R_100")
                            Dim tfD = If(qs("timeframe"), "1h")
                            Dim limD As Integer = 100
                            Integer.TryParse(qs("limit"), limD)
                            limD = Math.Max(1, Math.Min(limD, 1000))
                            Dim candles = Await DescargaService.ConsultarDatosAsync(symD, tfD, limD)
                            Dim arr As New JArray()
                            For Each c In candles
                                arr.Add(New JObject From {
                                    {"epoch", c.Epoch}, {"time", c.OpenTime},
                                    {"open", c.OpenPrice}, {"high", c.High},
                                    {"low", c.Low}, {"close", c.ClosePrice}
                                })
                            Next
                            result = New JObject From {
                                {"ok", True}, {"symbol", symD}, {"timeframe", tfD},
                                {"total", candles.Count}, {"candles", arr}
                            }
                        End If

                    ' ── Historial: contar registros en BD ─────────────────
                    Case "/api/historico/contar"
                        If DescargaService Is Nothing Then
                            result = ErrorJson("DescargaService no inicializado")
                        Else
                            Dim symC = If(qs("symbol"), "R_100")
                            Dim tfC = If(qs("timeframe"), "1h")
                            Dim cnt = Await DescargaService.ContarRegistrosAsync(symC, tfC)
                            result = New JObject From {
                                {"ok", True}, {"symbol", symC}, {"timeframe", tfC}, {"total_registros", cnt}
                            }
                        End If

                    ' ── Historial: detener descarga ───────────────────────
                    Case "/api/historico/detener"
                        If DescargaService IsNot Nothing Then DescargaService.Detener()
                        result = New JObject From {{"ok", True}, {"mensaje", "Señal de detención enviada"}}

                    ' ── Estrategias: catálogo ──────────────────────────────
                    Case "/api/estrategias"
                        result = New JObject From {
                            {"ok", True},
                            {"total", EstrategiaRegistry.GetAll().Count},
                            {"estrategias", EstrategiaRegistry.GetCatalog()}
                        }

                    ' ── Estrategias: ejecutar cualquier estrategia ─────────
                    Case "/api/estrategia"
                        Dim nomE = If(qs("nombre"), "EmaRsi")
                        Dim symE = If(qs("symbol"), "R_100")
                        Dim granE As Integer = 60
                        Integer.TryParse(qs("granularity"), granE)
                        Dim perE As Integer = 20
                        Integer.TryParse(qs("periodo"), perE)

                        Dim estrat = EstrategiaRegistry.GetByName(nomE)
                        If estrat Is Nothing Then
                            result = ErrorJson("Estrategia no encontrada: " & nomE & ". Usa /api/estrategias para ver el listado.")
                        Else
                            Dim minVelas = If(TypeOf estrat Is EstrategiaSeykota, perE + 25, 35)
                            Dim candlesE = Await ObtenerVelasComoCandles(symE, minVelas, granE)
                            If candlesE Is Nothing OrElse candlesE.Count < 10 Then
                                result = ErrorJson("Timeout o insuficientes velas para " & symE)
                            Else
                                Dim resE = estrat.Analizar(candlesE)
                                Dim datosArr As New JObject()
                                For Each kvp In resE.Datos
                                    datosArr(kvp.Key) = kvp.Value
                                Next
                                result = New JObject From {
                                    {"ok", True}, {"estrategia", estrat.Nombre},
                                    {"symbol", symE}, {"granularity", granE}, {"velas", candlesE.Count},
                                    {"senal", resE.Senal}, {"confianza", resE.Confianza},
                                    {"motivo", resE.Motivo}, {"datos", datosArr},
                                    {"timestamp", DateTime.UtcNow.ToString("o")}
                                }
                            End If
                        End If

                    Case Else
                        result = BuildHelp()
                End Select

                If LogAction IsNot Nothing Then
                    Dim okVal = result("ok")?.ToString()
                    Dim errVal = result("error")?.ToString()
                    LogAction.Invoke(String.Format("  → {0}", If(okVal = "True", "OK", "ERR: " & If(errVal, ""))))
                End If
                respCode = 200
                respBody = result.ToString(Formatting.Indented)

            Catch ex As JsonException
                respCode = 400
                respBody = ErrorJson("JSON invalido: " & ex.Message).ToString()
            Catch ex As Exception
                respCode = 500
                respBody = ErrorJson("Error interno: " & ex.Message).ToString()
            End Try

            Try
                Await WriteResponse(res, respCode, respBody)
            Catch
                ' Cliente desconectado antes de recibir la respuesta — ignorar
            End Try
        End Function

        ' ===================================================
        '  HELPER COMPARTIDO — Obtener velas como CandleModel
        ' ===================================================
        Private Async Function ObtenerVelasComoCandles(symbol As String, count As Integer, granularity As Integer) As Task(Of List(Of CandleModel))
            Dim ws As New ClientWebSocket()
            Dim cts As New CancellationTokenSource()
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15)
            Try
                Await ws.ConnectAsync(New Uri("wss://ws.derivws.com/websockets/v3?app_id=1089"), cts.Token)
                Dim reqObj As New JObject()
                reqObj.Add("ticks_history", symbol)
                reqObj.Add("count", count)
                reqObj.Add("end", "latest")
                reqObj.Add("granularity", granularity)
                reqObj.Add("style", "candles")
                Dim reqBytes = Encoding.UTF8.GetBytes(reqObj.ToString())
                Await ws.SendAsync(New ArraySegment(Of Byte)(reqBytes), WebSocketMessageType.Text, True, cts.Token)

                Dim buf(65535) As Byte
                Dim sbWs As New StringBuilder()
                Dim recvTask As Task(Of Boolean) = Task.Run(Async Function() As Task(Of Boolean)
                                                                Dim wsr As WebSocketReceiveResult
                                                                Do
                                                                    wsr = Await ws.ReceiveAsync(New ArraySegment(Of Byte)(buf), cts.Token)
                                                                    sbWs.Append(Encoding.UTF8.GetString(buf, 0, wsr.Count))
                                                                Loop While Not wsr.EndOfMessage
                                                                Return True
                                                            End Function)
                Dim winner = Await Task.WhenAny(recvTask, Task.Delay(8000))
                cts.Cancel()
                Try : ws.Dispose() : Catch : End Try
                If winner IsNot recvTask Then Return Nothing

                Dim respObj = JObject.Parse(sbWs.ToString())
                Dim candles = TryCast(respObj("candles"), JArray)
                If candles Is Nothing Then Return Nothing

                Dim lista As New List(Of CandleModel)()
                For Each tok As JToken In candles
                    Dim epoch As Long = CLng(tok("epoch"))
                    Dim dt = New DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(epoch).ToLocalTime()
                    lista.Add(New CandleModel With {
                        .Epoch = epoch,
                        .OpenTime = dt.ToString("yyyy-MM-dd HH:mm"),
                        .OpenPrice = CDec(tok("open")),
                        .High = CDec(tok("high")),
                        .Low = CDec(tok("low")),
                        .ClosePrice = CDec(tok("close"))
                    })
                Next
                Return lista
            Catch
                cts.Cancel()
                Try : ws.Dispose() : Catch : End Try
                Return Nothing
            End Try
        End Function

        ' ===================================================
        '  ANÁLISIS DE MERCADO — delega a EstrategiaEmaRsi
        ' ===================================================
        Private Async Function AnalizarMercadoAsync(symbol As String, periodo As Integer) As Task(Of JObject)
            Try
                Dim count = Math.Max(periodo, 30)
                Dim candles = Await ObtenerVelasComoCandles(symbol, count, 60)
                If candles Is Nothing OrElse candles.Count < 15 Then
                    Return ErrorJson("Timeout o insuficientes velas de " & symbol)
                End If

                Dim res = New EstrategiaEmaRsi().Analizar(candles)
                Dim ema5 = If(res.Datos.ContainsKey("EMA5"), res.Datos("EMA5"), 0D)
                Dim ema10 = If(res.Datos.ContainsKey("EMA10"), res.Datos("EMA10"), 0D)
                Dim rsi14 = If(res.Datos.ContainsKey("RSI14"), res.Datos("RSI14"), 50D)
                Dim score = If(res.Datos.ContainsKey("Score"), CInt(res.Datos("Score")), 0)

                Dim ultimas5 As New JArray()
                For Each c In candles.Skip(candles.Count - 5)
                    ultimas5.Add(New JObject From {
                        {"open", c.OpenPrice}, {"high", c.High}, {"low", c.Low},
                        {"close", c.ClosePrice},
                        {"dir", If(c.ClosePrice > c.OpenPrice, "UP", "DOWN")}
                    })
                Next

                Return New JObject From {
                    {"ok", True}, {"symbol", symbol}, {"velas", candles.Count},
                    {"ema5", ema5}, {"ema10", ema10}, {"rsi14", rsi14},
                    {"score", score}, {"confianza", res.Confianza}, {"senal", res.Senal},
                    {"motivo", res.Motivo},
                    {"ultimas5", ultimas5}, {"timestamp", DateTime.UtcNow.ToString("o")}
                }
            Catch ex As Exception
                Return ErrorJson("Error al analizar: " & ex.Message)
            End Try
        End Function

        ' ===================================================
        '  SEYKOTA — delega a EstrategiaSeykota
        ' ===================================================
        Private Async Function AnalizarSeykotaAsync(symbol As String, periodo As Integer, granularity As Integer) As Task(Of JObject)
            Try
                Dim candles = Await ObtenerVelasComoCandles(symbol, periodo + 25, granularity)
                If candles Is Nothing OrElse candles.Count < periodo + 5 Then
                    Return ErrorJson("Timeout o insuficientes velas de " & symbol)
                End If

                Dim res = New EstrategiaSeykota(periodo).Analizar(candles)
                Dim upper = If(res.Datos.ContainsKey("Upper"), res.Datos("Upper"), 0D)
                Dim lower = If(res.Datos.ContainsKey("Lower"), res.Datos("Lower"), 0D)
                Dim mid = If(res.Datos.ContainsKey("Mid"), res.Datos("Mid"), 0D)
                Dim atr14 = If(res.Datos.ContainsKey("ATR14"), res.Datos("ATR14"), 0D)

                Dim ultimas5 As New JArray()
                For Each c In candles.Skip(candles.Count - 5)
                    ultimas5.Add(New JObject From {
                        {"open", c.OpenPrice}, {"high", c.High}, {"low", c.Low},
                        {"close", c.ClosePrice},
                        {"dir", If(c.ClosePrice >= c.OpenPrice, "UP", "DOWN")}
                    })
                Next

                Return New JObject From {
                    {"ok", True}, {"symbol", symbol}, {"velas", candles.Count},
                    {"periodo", periodo}, {"granularity", granularity},
                    {"dc_upper", Math.Round(upper, 5)}, {"dc_lower", Math.Round(lower, 5)},
                    {"dc_mid", Math.Round(mid, 5)},
                    {"slope_up", res.Datos.ContainsKey("SlopeUp") AndAlso res.Datos("SlopeUp") = 1D},
                    {"slope_down", res.Datos.ContainsKey("SlopeDown") AndAlso res.Datos("SlopeDown") = 1D},
                    {"atr14", atr14},
                    {"precio_actual", If(res.Datos.ContainsKey("Precio"), Math.Round(res.Datos("Precio"), 5), 0D)},
                    {"senal", res.Senal}, {"confianza", res.Confianza}, {"motivo", res.Motivo},
                    {"ultimas5", ultimas5}, {"timestamp", DateTime.UtcNow.ToString("o")}
                }
            Catch ex As Exception
                Return ErrorJson("Error Seykota: " & ex.Message)
            End Try
        End Function

        ' ===================================================
        '  CALCULAR CUALQUIER INDICADOR DEL REGISTRY
        ' ===================================================
        Private Async Function EjecutarIndicadorAsync(symbol As String, nombre As String, count As Integer, granularity As Integer, ultimos As Integer) As Task(Of JObject)
            Try
                Dim ind = IndicatorRegistry.GetByName(nombre)
                If ind Is Nothing Then
                    Dim disponibles = String.Join(", ", IndicatorRegistry.GetAll().Select(Function(i) i.Name))
                    Return ErrorJson(String.Format("Indicador '{0}' no encontrado. Disponibles: {1}", nombre, disponibles))
                End If

                Dim candles = Await ObtenerVelasComoCandles(symbol, count, granularity)
                If candles Is Nothing OrElse candles.Count = 0 Then
                    Return ErrorJson("Timeout o sin velas para " & symbol)
                End If

                Dim resultados = ind.Calculate(candles)
                Dim tomar = Math.Min(ultimos, resultados.Count)
                Dim recientes = resultados.Skip(resultados.Count - tomar).ToList()

                Dim arr As New JArray()
                For Each r In recientes
                    Dim item As New JObject()
                    item("epoch") = r.Epoch
                    item("openTime") = r.OpenTime
                    item("valor") = If(r.IndicatorValue.HasValue, New JValue(CDbl(Math.Round(r.IndicatorValue.Value, 5))), CType(JValue.CreateNull(), JToken))
                    If r.AdditionalValues IsNot Nothing AndAlso r.AdditionalValues.Count > 0 Then
                        Dim extras As New JObject()
                        For Each kv In r.AdditionalValues
                            extras(kv.Key) = If(kv.Value.HasValue, New JValue(CDbl(Math.Round(kv.Value.Value, 5))), CType(JValue.CreateNull(), JToken))
                        Next
                        item("extra") = extras
                    End If
                    arr.Add(item)
                Next

                Return New JObject From {
                    {"ok", True}, {"symbol", symbol}, {"indicador", ind.Name},
                    {"categoria", ind.Category}, {"descripcion", ind.Description},
                    {"granularity", granularity}, {"velas_usadas", candles.Count},
                    {"resultados", arr}, {"timestamp", DateTime.UtcNow.ToString("o")}
                }
            Catch ex As Exception
                Return ErrorJson("Error calculando " & nombre & ": " & ex.Message)
            End Try
        End Function

        ' ===================================================
        '  HELPERS
        ' ===================================================
        Private Async Function WriteResponse(res As HttpListenerResponse, code As Integer, json As String) As Task
            res.StatusCode = code
            Dim bytes = Encoding.UTF8.GetBytes(json)
            res.ContentLength64 = bytes.Length
            Await res.OutputStream.WriteAsync(bytes, 0, bytes.Length)
            res.OutputStream.Close()
        End Function

        Private Function ErrorJson(msg As String) As JObject
            Return New JObject From {{"ok", False}, {"error", msg}}
        End Function

        Private Function EstadoComoJson(est As DescargaJobEstado) As JObject
            Return New JObject From {
                {"ok", True},
                {"symbol", est.Symbol},
                {"timeframe", est.Timeframe},
                {"desde", est.Desde},
                {"hasta", est.Hasta},
                {"esta_activo", est.EstaActivo},
                {"velas_guardadas", est.VelasGuardadas},
                {"pagina", est.Pagina},
                {"mensaje", est.UltimoMensaje},
                {"completado", est.Completado},
                {"tuvo_error", est.TuvoError},
                {"inicio_utc", est.InicioUtc}
            }
        End Function

        Private Function BuildHelp() As JObject
            Dim h As New JObject()
            h("ok") = True
            h("server") = "Deriv Trading API Server"

            Dim ep As New JObject()
            ' ── Estado y operaciones
            ep("GET  /api/status") = "Estado de conexion y balance"
            ep("GET  /api/operations") = "Lista de operaciones de sesion"
            ' ── Análisis y estrategias
            ep("GET  /api/analizar") = "EmaRsi. Params: ?symbol=R_100&periodo=20&min_confianza=40"
            ep("GET  /api/seykota") = "Seykota. Params: ?symbol=R_100&periodo=20&granularity=60"
            ep("GET  /api/estrategias") = "Catalogo de estrategias disponibles"
            ep("GET  /api/estrategia") = "Ejecutar estrategia. Params: ?nombre=EmaRsi&symbol=R_100&granularity=60&periodo=20"
            ep("POST /api/analizar-y-operar") = "Analiza y ejecuta si hay senal. Ver body_analizar_operar."
            ' ── Indicadores
            ep("GET  /api/indicadores") = "Catalogo de los 24 indicadores del registry"
            ep("GET  /api/indicador") = "Calcular indicador. Params: ?symbol=R_100&nombre=RSI&granularity=60&ultimos=5"
            ' ── Trading
            ep("POST /api/trade") = "Ejecutar operacion. Ver body_trade."
            ep("POST /api/sell") = "Cerrar contrato. Body: {contract_id}"
            ' ── Historial
            ep("GET  /api/historico/descargar") = "Iniciar descarga. Params: ?symbol=R_100&timeframe=1h&desde=2024-01-01&hasta=2024-12-31"
            ep("GET  /api/historico/estado") = "Estado del job de descarga actual"
            ep("GET  /api/historico/datos") = "Velas de BD. Params: ?symbol=R_100&timeframe=1h&limit=100"
            ep("GET  /api/historico/contar") = "Total de registros. Params: ?symbol=R_100&timeframe=1h"
            ep("GET  /api/historico/detener") = "Cancelar descarga en curso"
            ep("GET  /api/help") = "Esta ayuda"
            h("endpoints") = ep

            h("body_analizar_operar") = JObject.Parse("{""symbol"":""R_100"",""amount"":1,""duration"":5,""duration_unit"":""t"",""periodo"":20,""min_confianza"":40}")
            h("body_trade") = JObject.Parse("{""contract_type"":""CALL"",""symbol"":""R_100"",""amount"":1,""basis"":""stake"",""currency"":""USD"",""duration"":5,""duration_unit"":""t""}")
            h("contract_types") = JArray.Parse("[""CALL"",""PUT"",""ONETOUCH"",""NOTOUCH"",""DIGITEVEN"",""DIGITODD"",""DIGITOVER"",""DIGITUNDER"",""DIGITMATCH"",""DIGITDIFF""]")
            h("duration_units") = JObject.Parse("{""t"":""ticks"",""s"":""segundos"",""m"":""minutos"",""h"":""horas"",""d"":""dias""}")
            h("symbols") = JArray.Parse("[""R_10"",""R_25"",""R_50"",""R_75"",""R_100"",""1HZ100V"",""1HZ75V"",""1HZ50V""]")
            Return h
        End Function

    End Class
End Namespace

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
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Indicators
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting

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

        ''' <summary>Motor de live testing. Inyectado por MainWindow al arrancar el servidor.</summary>
        Public Property LiveTestMotor As LiveTestMotor = Nothing

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

                    ' ─────────────────────────────────────────────────────────
                    '  ESTRATEGIAS DINAMICAS  /api/dinamicas/*
                    ' ─────────────────────────────────────────────────────────
                    Case "/api/dinamicas/listar"
                        Dim cat = EstrategiasDinamicasRegistry.GetCatalog()
                        result = New JObject From {
                            {"ok", True},
                            {"total", cat.Count},
                            {"directorio", EstrategiasDinamicasRegistry.Directorio},
                            {"estrategias", cat}
                        }

                    Case "/api/dinamicas/leer"
                        Dim nomLeer = If(qs("nombre"), "")
                        If String.IsNullOrWhiteSpace(nomLeer) Then
                            result = ErrorJson("Parametro requerido: nombre")
                        Else
                            Dim estLeer = EstrategiasDinamicasRegistry.GetByNombre(nomLeer)
                            If estLeer Is Nothing Then
                                result = ErrorJson("Estrategia no encontrada: " & nomLeer)
                            Else
                                Dim pathJson = estLeer.Definicion.ArchivoOrigen
                                Dim contenido = If(File.Exists(pathJson), File.ReadAllText(pathJson, Encoding.UTF8), "{}")
                                result = New JObject From {
                                    {"ok", True},
                                    {"nombre", estLeer.Nombre},
                                    {"archivo", System.IO.Path.GetFileName(pathJson)},
                                    {"contenido", contenido}
                                }
                            End If
                        End If

                    Case "/api/dinamicas/guardar"
                        If method <> "POST" Then
                            result = ErrorJson("Use POST /api/dinamicas/guardar")
                        ElseIf body Is Nothing OrElse body("nombre") Is Nothing OrElse body("contenido") Is Nothing Then
                            result = ErrorJson("Body requerido: {nombre, contenido}")
                        Else
                            Dim nomGuardar = body("nombre").ToString().Trim()
                            Dim contenidoGuardar = body("contenido").ToString()
                            If String.IsNullOrWhiteSpace(nomGuardar) Then
                                result = ErrorJson("nombre no puede estar vacio")
                            Else
                                Dim dirGuardar = EstrategiasDinamicasRegistry.Directorio
                                If Not Directory.Exists(dirGuardar) Then Directory.CreateDirectory(dirGuardar)
                                ' Nombre de archivo = nombre.json (sanitizar)
                                Dim nombreArchivo = nomGuardar.Replace(" ", "_").Replace("/", "_").Replace("\", "_") & ".json"
                                Dim pathGuardar = System.IO.Path.Combine(dirGuardar, nombreArchivo)
                                File.WriteAllText(pathGuardar, contenidoGuardar, Encoding.UTF8)
                                ' El FileSystemWatcher recargara automaticamente
                                result = New JObject From {
                                    {"ok", True},
                                    {"nombre", nomGuardar},
                                    {"archivo", nombreArchivo},
                                    {"path", pathGuardar}
                                }
                            End If
                        End If

                    Case "/api/dinamicas/eliminar"
                        If method <> "POST" Then
                            result = ErrorJson("Use POST /api/dinamicas/eliminar")
                        ElseIf body Is Nothing OrElse body("nombre") Is Nothing Then
                            result = ErrorJson("Body requerido: {nombre}")
                        Else
                            Dim nomEliminar = body("nombre").ToString()
                            Dim estElim = EstrategiasDinamicasRegistry.GetByNombre(nomEliminar)
                            If estElim Is Nothing Then
                                result = ErrorJson("Estrategia no encontrada: " & nomEliminar)
                            Else
                                Dim pathElim = estElim.Definicion.ArchivoOrigen
                                If File.Exists(pathElim) Then File.Delete(pathElim)
                                result = New JObject From {
                                    {"ok", True},
                                    {"nombre", nomEliminar},
                                    {"mensaje", "Estrategia eliminada"}
                                }
                            End If
                        End If

                    ' ── DEBUG: evalua estrategia sobre ultimas velas de BD ─────────────────
                    Case "/api/dinamicas/debug"
                        Dim nomDbg = If(qs("nombre"), "")
                        Dim symDbg = If(qs("symbol"), "R_100")
                        Dim tfDbg  = If(qs("timeframe"), "1m")
                        Dim limDbg As Integer = 100 : Integer.TryParse(qs("ventana"), limDbg)
                        If String.IsNullOrWhiteSpace(nomDbg) Then
                            result = ErrorJson("Parametro requerido: nombre")
                        Else
                            Dim estDbg = EstrategiasDinamicasRegistry.GetByNombre(nomDbg)
                            If estDbg Is Nothing Then
                                result = ErrorJson("Estrategia no encontrada: " & nomDbg)
                            Else
                                Try
                                    Dim repoDbg As New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString())
                                    Dim candlesDbg = Await repoDbg.GetCandlesAsync("Deriv", symDbg, tfDbg, limDbg)
                                    Dim resDbg = estDbg.Analizar(candlesDbg)
                                    Dim datosArr As New JObject()
                                    For Each kv In resDbg.Datos
                                        datosArr(kv.Key) = CDbl(kv.Value)
                                    Next
                                    result = New JObject From {
                                        {"ok",         True},
                                        {"estrategia", nomDbg},
                                        {"symbol",     symDbg},
                                        {"timeframe",  tfDbg},
                                        {"velas",      candlesDbg.Count},
                                        {"senal",      resDbg.Senal},
                                        {"confianza",  resDbg.Confianza},
                                        {"motivo",     resDbg.Motivo},
                                        {"datos",      datosArr}
                                    }
                                Catch ex As Exception
                                    result = ErrorJson("Error debug: " & ex.Message)
                                End Try
                            End If
                        End If

                    Case "/api/dinamicas/backtest"
                        Dim nomBt = If(qs("nombre"), "")
                        If String.IsNullOrWhiteSpace(nomBt) Then
                            result = ErrorJson("Parametro requerido: nombre")
                        Else
                            Dim estBt = EstrategiasDinamicasRegistry.GetByNombre(nomBt)
                            If estBt Is Nothing Then
                                result = ErrorJson("Estrategia no encontrada: " & nomBt)
                            Else
                                Try
                                    Dim symBt   = If(qs("symbol"), "R_100")
                                    Dim tfBt    = If(qs("timeframe"), "1h")
                                    ' Usar If(..., "0") para que TryParse no aplaste el default cuando el param es Nothing
                                    Dim desdeBt As Long = 0    : Long.TryParse(If(qs("desde_epoch"), "0"), desdeBt)
                                    Dim hastaBt As Long = 0    : Long.TryParse(If(qs("hasta_epoch"), "0"), hastaBt)
                                    Dim durBt   As Integer = 5 : Integer.TryParse(If(qs("duracion_velas"), "5"), durBt)
                                    Dim payBt   As Decimal = 85D : Decimal.TryParse(If(qs("payout"), "85"), payBt)
                                    Dim confBt  As Integer = 55  : Integer.TryParse(If(qs("min_confianza"), "55"), confBt)
                                    Dim montoBt As Decimal = 10D : Decimal.TryParse(If(qs("monto"), "10"), montoBt)
                                    Dim ventanaBt As Integer = 100 : Integer.TryParse(If(qs("ventana"), "100"), ventanaBt)

                                    If hastaBt = 0 Then hastaBt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                    ' Si no se especifica desde_epoch, usar 0 para cargar todo el historial disponible
                                    ' (evita el error de 0 velas cuando los datos son mas antiguos que 30 dias)

                                    Dim configBt As New BacktestConfig With {
                                        .Symbol             = symBt,
                                        .Timeframe          = tfBt,
                                        .DesdeEpoch         = desdeBt,
                                        .HastaEpoch         = hastaBt,
                                        .DuracionVelas      = durBt,
                                        .PayoutPct          = payBt,
                                        .MinConfianza       = confBt,
                                        .VentanaIndicadores = ventanaBt,
                                        .Cuentas = New List(Of BacktestCuenta) From {
                                            New BacktestCuenta With {
                                                .NombreCuenta      = "Simulacion",
                                                .BalanceInicial    = 10000D,
                                                .BalanceActual     = 10000D,
                                                .MontoPorOperacion = montoBt
                                            }
                                        }
                                    }

                                    Dim motorBt As New BacktestMotor()
                                    Dim resBt = Await motorBt.EjecutarAsync(configBt, estBt)

                                    ' BacktestMotor ya guarda en BD y setea resBt.Sesion.Id
                                    ' No guardar de nuevo para evitar sesiones duplicadas
                                    Dim s = resBt.Sesion
                                    result = New JObject From {
                                        {"ok",               True},
                                        {"sesion_id",        s.Id},
                                        {"estrategia",       s.Estrategia},
                                        {"symbol",           s.Symbol},
                                        {"timeframe",        s.Timeframe},
                                        {"total_senales",    s.TotalSenales},
                                        {"total_ops",        s.TotalOperaciones},
                                        {"win_rate",         Math.Round(s.WinRate, 4)},
                                        {"profit_loss",      Math.Round(s.ProfitLossTotal, 4)},
                                        {"max_drawdown",     Math.Round(s.MaxDrawdown, 4)},
                                        {"profit_factor",    Math.Round(s.ProfitFactor, 4)},
                                        {"ganadas",          resBt.OperacionesGanadas.Count},
                                        {"perdidas",         resBt.OperacionesPerdidas.Count}
                                    }
                                Catch ex As Exception
                                    result = ErrorJson("Error en backtest: " & ex.Message)
                                End Try
                            End If
                        End If

                    Case "/api/dinamicas/sesiones"
                        Dim limSes As Integer = 20 : Integer.TryParse(qs("limite"), limSes)
                        Dim filtroEst = If(qs("estrategia"), "")
                        Dim repoSes As New BacktestRepository()
                        Dim sesionesSes = If(String.IsNullOrWhiteSpace(filtroEst),
                                             Await repoSes.GetSesionesAsync(limSes),
                                             Await repoSes.GetSesionesPorEstrategiaAsync(filtroEst, limSes))
                        Dim arrSes As New JArray()
                        For Each ss In sesionesSes
                            Dim desdeStr = If(ss.DesdeEpoch > 0,
                                             DateTimeOffset.FromUnixTimeSeconds(ss.DesdeEpoch).ToString("dd/MM/yy"), "")
                            Dim hastaStr = If(ss.HastaEpoch > 0,
                                             DateTimeOffset.FromUnixTimeSeconds(ss.HastaEpoch).ToString("dd/MM/yy"), "")
                            arrSes.Add(New JObject From {
                                {"id",            ss.Id},
                                {"estrategia",    ss.Estrategia},
                                {"symbol",        ss.Symbol},
                                {"timeframe",     ss.Timeframe},
                                {"duracion_velas",ss.DuracionVelas},
                                {"payout_pct",    ss.PayoutPct},
                                {"min_confianza", ss.MinConfianza},
                                {"desde_epoch",   ss.DesdeEpoch},
                                {"hasta_epoch",   ss.HastaEpoch},
                                {"desde_str",     desdeStr},
                                {"hasta_str",     hastaStr},
                                {"total_senales", ss.TotalSenales},
                                {"total_ops",     ss.TotalOperaciones},
                                {"win_rate",      Math.Round(ss.WinRate * 100D, 2)},
                                {"profit_loss",   Math.Round(ss.ProfitLossTotal, 4)},
                                {"max_drawdown",  Math.Round(ss.MaxDrawdown, 4)},
                                {"profit_factor", Math.Round(ss.ProfitFactor, 4)},
                                {"created_at",    ss.CreatedAt.ToString("yyyy-MM-dd HH:mm")}
                            })
                        Next
                        result = New JObject From {{"ok", True}, {"total", arrSes.Count},
                                                   {"filtro_estrategia", filtroEst}, {"sesiones", arrSes}}

                    Case "/api/dinamicas/historial"
                        ' Evolucion temporal de una estrategia: sesiones ordenadas ASC con deltas
                        Dim nomHist = If(qs("nombre"), "")
                        If String.IsNullOrWhiteSpace(nomHist) Then
                            result = ErrorJson("Parametro requerido: nombre")
                        Else
                            Dim limHist As Integer = 50 : Integer.TryParse(qs("limite"), limHist)
                            Dim repoHist As New BacktestRepository()
                            Dim sesHist = Await repoHist.GetSesionesPorEstrategiaAsync(nomHist, limHist)
                            Dim arrHist As New JArray()
                            Dim prevWr As Decimal = -1D
                            Dim prevPnl As Decimal = 0D
                            For i = 0 To sesHist.Count - 1
                                Dim ss = sesHist(i)
                                Dim wrPct = Math.Round(ss.WinRate * 100D, 2)
                                Dim deltaWr  = If(prevWr < 0, 0D, Math.Round(wrPct  - prevWr,  2))
                                Dim deltaPnl = If(i = 0,      0D, Math.Round(ss.ProfitLossTotal - prevPnl, 4))
                                Dim tendencia = If(deltaWr > 1, "mejora", If(deltaWr < -1, "degrada", "estable"))
                                arrHist.Add(New JObject From {
                                    {"sesion",         i + 1},
                                    {"id",             ss.Id},
                                    {"symbol",         ss.Symbol},
                                    {"timeframe",      ss.Timeframe},
                                    {"total_ops",      ss.TotalOperaciones},
                                    {"win_rate",       wrPct},
                                    {"delta_win_rate", deltaWr},
                                    {"profit_loss",    Math.Round(ss.ProfitLossTotal, 4)},
                                    {"delta_pnl",      deltaPnl},
                                    {"max_drawdown",   Math.Round(ss.MaxDrawdown, 4)},
                                    {"profit_factor",  Math.Round(ss.ProfitFactor, 4)},
                                    {"min_confianza",  ss.MinConfianza},
                                    {"tendencia",      tendencia},
                                    {"created_at",     ss.CreatedAt.ToString("yyyy-MM-dd HH:mm")}
                                })
                                prevWr  = wrPct
                                prevPnl = ss.ProfitLossTotal
                            Next
                            ' Resumen de tendencia global
                            Dim resumen As String = "sin_datos"
                            If sesHist.Count >= 2 Then
                                Dim primeraWr = CDec(sesHist(0).WinRate * 100D)
                                Dim ultimaWr  = CDec(sesHist(sesHist.Count - 1).WinRate * 100D)
                                Dim diff = ultimaWr - primeraWr
                                resumen = If(diff > 2, "mejorando", If(diff < -2, "degradando", "estable"))
                            End If
                            result = New JObject From {
                                {"ok",        True},
                                {"estrategia",nomHist},
                                {"total",     sesHist.Count},
                                {"tendencia_global", resumen},
                                {"mejor_win_rate",   If(sesHist.Count > 0, Math.Round(sesHist.Max(Function(x) x.WinRate) * 100D, 2), 0)},
                                {"peor_win_rate",    If(sesHist.Count > 0, Math.Round(sesHist.Min(Function(x) x.WinRate) * 100D, 2), 0)},
                                {"historial",  arrHist}
                            }
                        End If

                    Case "/api/dinamicas/comparar"
                        ' Comparacion lado a lado de dos sesiones
                        Dim idA As Integer = 0 : Integer.TryParse(qs("sesion_a"), idA)
                        Dim idB As Integer = 0 : Integer.TryParse(qs("sesion_b"), idB)
                        If idA = 0 OrElse idB = 0 Then
                            result = ErrorJson("Parametros requeridos: sesion_a, sesion_b")
                        Else
                            Dim repoCmp As New BacktestRepository()
                            Dim sA = Await repoCmp.GetSesionPorIdAsync(idA)
                            Dim sB = Await repoCmp.GetSesionPorIdAsync(idB)
                            If sA Is Nothing OrElse sB Is Nothing Then
                                result = ErrorJson("Una o ambas sesiones no encontradas")
                            Else
                                Dim wrA = Math.Round(sA.WinRate * 100D, 2)
                                Dim wrB = Math.Round(sB.WinRate * 100D, 2)
                                result = New JObject From {
                                    {"ok", True},
                                    {"sesion_a", New JObject From {
                                        {"id",            sA.Id},
                                        {"estrategia",    sA.Estrategia},
                                        {"symbol",        sA.Symbol},
                                        {"timeframe",     sA.Timeframe},
                                        {"total_ops",     sA.TotalOperaciones},
                                        {"win_rate",      wrA},
                                        {"profit_loss",   Math.Round(sA.ProfitLossTotal, 4)},
                                        {"max_drawdown",  Math.Round(sA.MaxDrawdown, 4)},
                                        {"profit_factor", Math.Round(sA.ProfitFactor, 4)},
                                        {"min_confianza", sA.MinConfianza},
                                        {"created_at",    sA.CreatedAt.ToString("yyyy-MM-dd HH:mm")}
                                    }},
                                    {"sesion_b", New JObject From {
                                        {"id",            sB.Id},
                                        {"estrategia",    sB.Estrategia},
                                        {"symbol",        sB.Symbol},
                                        {"timeframe",     sB.Timeframe},
                                        {"total_ops",     sB.TotalOperaciones},
                                        {"win_rate",      wrB},
                                        {"profit_loss",   Math.Round(sB.ProfitLossTotal, 4)},
                                        {"max_drawdown",  Math.Round(sB.MaxDrawdown, 4)},
                                        {"profit_factor", Math.Round(sB.ProfitFactor, 4)},
                                        {"min_confianza", sB.MinConfianza},
                                        {"created_at",    sB.CreatedAt.ToString("yyyy-MM-dd HH:mm")}
                                    }},
                                    {"delta", New JObject From {
                                        {"win_rate",      Math.Round(wrB - wrA, 2)},
                                        {"profit_loss",   Math.Round(sB.ProfitLossTotal - sA.ProfitLossTotal, 4)},
                                        {"max_drawdown",  Math.Round(sB.MaxDrawdown - sA.MaxDrawdown, 4)},
                                        {"profit_factor", Math.Round(sB.ProfitFactor - sA.ProfitFactor, 4)},
                                        {"total_ops",     sB.TotalOperaciones - sA.TotalOperaciones},
                                        {"veredicto",     If(wrB > wrA + 2, "B_mejor", If(wrA > wrB + 2, "A_mejor", "similar"))}
                                    }}
                                }
                            End If
                        End If

                    Case "/api/dinamicas/operaciones"
                        Dim sidOps As Integer = 0 : Integer.TryParse(If(qs("sesion_id"), "0"), sidOps)
                        If sidOps = 0 Then
                            result = ErrorJson("Parametro requerido: sesion_id")
                        Else
                            Dim limOps As Integer = 500 : Integer.TryParse(If(qs("limite"), "500"), limOps)
                            Dim repoOps As New BacktestRepository()
                            ' Sesion para nombre de estrategia y total real
                            Dim sesionOps = Await repoOps.GetSesionPorIdAsync(sidOps)
                            Dim estNombreOps = If(sesionOps IsNot Nothing, sesionOps.Estrategia, "")
                            Dim totalRealOps = If(sesionOps IsNot Nothing, CLng(sesionOps.TotalOperaciones), 0L)
                            Dim estVersionOps = ""
                            If estNombreOps <> "" Then
                                Dim estRegOps = EstrategiasDinamicasRegistry.GetByNombre(estNombreOps)
                                If estRegOps IsNot Nothing Then estVersionOps = "v" & estRegOps.Definicion.Version
                            End If
                            ' Cargar solo las ultimas N para no saturar la respuesta
                            Dim opsList = Await repoOps.GetUltimasOperacionesAsync(sidOps, limOps)
                            Dim arrOps As New JArray()
                            For Each op In opsList
                                arrOps.Add(New JObject From {
                                    {"estrategia",   estNombreOps},
                                    {"version",      estVersionOps},
                                    {"tipo",         op.Tipo},
                                    {"epoch",        op.EpochEntrada},
                                    {"entrada",      op.PrecioEntrada},
                                    {"salida",       op.PrecioSalida},
                                    {"estado",       op.Estado},
                                    {"pnl",          op.ProfitLoss},
                                    {"confianza",    op.Confianza},
                                    {"balance_post", op.BalancePost},
                                    {"cuenta",       op.CuentaNombre}
                                })
                            Next
                            result = New JObject From {
                                {"ok",           True},
                                {"sesion_id",    sidOps},
                                {"total",        totalRealOps},
                                {"mostrando",    arrOps.Count},
                                {"operaciones",  arrOps}
                            }
                        End If

                    Case "/api/dinamicas/perdidas"
                        Dim sidPerd As Integer = 0 : Integer.TryParse(qs("sesion_id"), sidPerd)
                        If sidPerd = 0 Then
                            result = ErrorJson("Parametro requerido: sesion_id")
                        Else
                            Dim repoPerd As New BacktestRepository()
                            Dim perdList = Await repoPerd.GetPerdidasConContextoAsync(sidPerd)
                            Dim arrPerd As New JArray()
                            For Each op In perdList
                                arrPerd.Add(New JObject From {
                                    {"tipo",              op.Tipo},
                                    {"epoch",             op.EpochEntrada},
                                    {"confianza",         op.Confianza},
                                    {"motivo",            op.MotivoSenal},
                                    {"datos_indicadores", op.DatosIndicadores},
                                    {"contexto_velas",    op.ContextoVelas}
                                })
                            Next
                            result = New JObject From {{"ok", True}, {"sesion_id", sidPerd}, {"total", arrPerd.Count}, {"perdidas", arrPerd}}
                        End If

                    Case "/api/dinamicas/live/iniciar"
                        If method <> "POST" Then
                            result = ErrorJson("Use POST /api/dinamicas/live/iniciar")
                        ElseIf body Is Nothing OrElse body("nombre") Is Nothing Then
                            result = ErrorJson("Body requerido: {nombre, symbol, timeframe, ...}")
                        Else
                            Dim nomLive = body("nombre").ToString()
                            Dim estLive = EstrategiasDinamicasRegistry.GetByNombre(nomLive)
                            If estLive Is Nothing Then
                                result = ErrorJson("Estrategia no encontrada: " & nomLive)
                            Else
                                If LiveTestMotor Is Nothing Then
                                    LiveTestMotor = New LiveTestMotor()
                                    LiveTestMotor.ExecuteTrade = ExecuteTrade
                                End If
                                Dim cfgLive As New LiveTestConfig With {
                                    .Symbol           = If(body("symbol")?.ToString(), "R_100"),
                                    .Timeframe        = If(body("timeframe")?.ToString(), "1h"),
                                    .DuracionVelas    = If(body("duracion_velas") IsNot Nothing, CInt(body("duracion_velas")), 5),
                                    .PayoutPct        = If(body("payout") IsNot Nothing, CDec(body("payout")), 85D),
                                    .MinConfianza     = If(body("min_confianza") IsNot Nothing, CInt(body("min_confianza")), 55),
                                    .MontoOperacion   = If(body("monto") IsNot Nothing, CDec(body("monto")), 1D),
                                    .VentanaIndicadores = If(body("ventana") IsNot Nothing, CInt(body("ventana")), 100)
                                }
                                Dim sesIdLive = Await LiveTestMotor.IniciarAsync(cfgLive, estLive)
                                result = New JObject From {
                                    {"ok",         True},
                                    {"sesion_id",  sesIdLive},
                                    {"estrategia", nomLive},
                                    {"symbol",     cfgLive.Symbol},
                                    {"timeframe",  cfgLive.Timeframe},
                                    {"mensaje",    "Live test iniciado. Conecta el streaming para empezar a recibir velas."}
                                }
                            End If
                        End If

                    Case "/api/dinamicas/live/detener"
                        If LiveTestMotor IsNot Nothing AndAlso LiveTestMotor.EstaActivo Then
                            Await LiveTestMotor.DetenerAsync()
                            result = New JObject From {{"ok", True}, {"mensaje", "Live test detenido."}}
                        Else
                            result = New JObject From {{"ok", False}, {"mensaje", "No hay live test activo."}}
                        End If

                    Case "/api/dinamicas/live/estado"
                        If LiveTestMotor IsNot Nothing Then
                            Dim estadoLive = LiveTestMotor.GetEstado()
                            estadoLive("ok") = True
                            result = estadoLive
                        Else
                            result = New JObject From {{"ok", True}, {"activo", False}, {"mensaje", "Motor no inicializado."}}
                        End If

                    ' ── Eliminar sesion + operaciones ────────────────────────────
                    Case "/api/dinamicas/sesiones/eliminar"
                        If method <> "POST" Then
                            result = ErrorJson("Use POST /api/dinamicas/sesiones/eliminar")
                        ElseIf body Is Nothing OrElse body("sesion_id") Is Nothing Then
                            result = ErrorJson("Parametro requerido: sesion_id")
                        Else
                            Dim sidDel As Integer = CInt(body("sesion_id"))
                            Try
                                Dim repoDel As New BacktestRepository()
                                Await repoDel.EliminarSesionAsync(sidDel)
                                result = New JObject From {{"ok", True}, {"sesion_id", sidDel},
                                                           {"mensaje", String.Format("Sesion #{0} eliminada.", sidDel)}}
                            Catch exDel As Exception
                                result = ErrorJson("Error eliminando sesion: " & exDel.Message)
                            End Try
                        End If

                    ' ── Catalogo de activos para descarga masiva ──────────────────
                    Case "/api/activos"
                        Dim activosFile = IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "activos_descarga.json")
                        If method = "POST" Then
                            ' Guardar catalogo recibido en el body
                            If body Is Nothing Then
                                result = ErrorJson("Body JSON requerido.")
                            Else
                                Try
                                    IO.File.WriteAllText(activosFile, body.ToString(Formatting.Indented), System.Text.Encoding.UTF8)
                                    result = New JObject From {{"ok", True}, {"mensaje", "Catalogo guardado."}}
                                Catch exW As Exception
                                    result = ErrorJson("Error guardando: " & exW.Message)
                                End Try
                            End If
                        Else
                            ' Devolver catalogo: desde archivo si existe, sino el default integrado
                            Dim catalogoJson As JObject = Nothing
                            If IO.File.Exists(activosFile) Then
                                Try
                                    catalogoJson = JObject.Parse(IO.File.ReadAllText(activosFile, System.Text.Encoding.UTF8))
                                Catch
                                    catalogoJson = Nothing
                                End Try
                            End If
                            If catalogoJson Is Nothing Then
                                ' Catalogo por defecto — incluye symbol (Deriv), yahoo y stooq
                                ' para que tanto descargar_masivo.ps1 como importar_historico.ps1
                                ' puedan usar el mismo endpoint como fuente de verdad.
                                catalogoJson = JObject.Parse("{
                                    ""timeframes"": [""1d"",""4h"",""1h"",""5m""],
                                    ""activos"": [
                                        {""nombre"":""EURUSD"",""symbol"":""frxEURUSD"",""yahoo"":""EURUSD=X"",""stooq"":""eurusd""},
                                        {""nombre"":""GBPUSD"",""symbol"":""frxGBPUSD"",""yahoo"":""GBPUSD=X"",""stooq"":""gbpusd""},
                                        {""nombre"":""USDCHF"",""symbol"":""frxUSDCHF"",""yahoo"":""USDCHF=X"",""stooq"":""usdchf""},
                                        {""nombre"":""USDJPY"",""symbol"":""frxUSDJPY"",""yahoo"":""USDJPY=X"",""stooq"":""usdjpy""},
                                        {""nombre"":""GBPJPY"",""symbol"":""frxGBPJPY"",""yahoo"":""GBPJPY=X"",""stooq"":""gbpjpy""},
                                        {""nombre"":""XAUUSD"",""symbol"":""frxXAUUSD"",""yahoo"":""GC=F"",   ""stooq"":""xauusd""},
                                        {""nombre"":""XAGUSD"",""symbol"":""frxXAGUSD"",""yahoo"":""SI=F"",   ""stooq"":""xagusd""},
                                        {""nombre"":""OIL_US"",""symbol"":""frxUSOIL"", ""yahoo"":""CL=F"",   ""stooq"":""cl.f""},
                                        {""nombre"":""NAS100"",""symbol"":""OTC_NDX"",  ""yahoo"":""NQ=F"",   ""stooq"":""\^ndx""},
                                        {""nombre"":""SP500"", ""symbol"":""OTC_SPC"",  ""yahoo"":""ES=F"",   ""stooq"":""\^spx""}
                                    ]
                                }")
                            End If
                            catalogoJson("ok") = True
                            catalogoJson("fuente") = If(IO.File.Exists(activosFile), "archivo", "default")
                            result = catalogoJson
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
            ' ── Estrategias dinamicas
            ep("GET  /api/dinamicas/listar") = "Catalogo de estrategias JSON disponibles"
            ep("GET  /api/dinamicas/leer") = "Leer JSON de estrategia. Params: ?nombre=EmaRsiCruce"
            ep("POST /api/dinamicas/guardar") = "Crear/actualizar JSON. Body: {nombre, contenido}"
            ep("POST /api/dinamicas/eliminar") = "Eliminar JSON. Body: {nombre}"
            ep("GET  /api/dinamicas/backtest") = "Ejecutar backtest. Params: ?nombre=EmaRsiCruce&symbol=R_100&timeframe=1h&desde_epoch=...&hasta_epoch=...&duracion_velas=5&payout=85&min_confianza=55&monto=10"
            ep("GET  /api/dinamicas/sesiones") = "Listar sesiones. Params: ?limite=20"
            ep("GET  /api/dinamicas/operaciones") = "Operaciones de sesion. Params: ?sesion_id=1"
            ep("GET  /api/dinamicas/perdidas") = "Perdidas con contexto. Params: ?sesion_id=1"
            ep("POST /api/dinamicas/live/iniciar") = "Iniciar live test. Body: {nombre, symbol, timeframe, duracion_velas, payout, min_confianza, monto}"
            ep("POST /api/dinamicas/live/detener") = "Detener live test activo"
            ep("GET  /api/dinamicas/live/estado") = "Estado del live test activo"
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

' ============================================================
' HeavyApiServer.vb
'
' Servidor HTTP embebido (HttpListener) dedicado al modulo Heavy
' Strategy Tester. Permite que herramientas externas (como Claude)
' suban combinaciones de pruebas, las ejecuten y consulten los
' resultados, sin tocar el TradingApiServer principal (puerto 8765).
'
' Puerto sugerido: 8766
'
' Endpoints:
'   GET  /api/heavy/help                — documentacion JSON
'   GET  /api/heavy/status              — estado de cola y ultima ejecucion
'   GET  /api/heavy/activos             — symbols disponibles (DISTINCT candles)
'   GET  /api/heavy/estrategias         — catalogo de estrategias dinamicas
'   GET  /api/heavy/timeframes          — TFs soportados
'   POST /api/heavy/jobs/agregar        — agrega N combinaciones via HeavySelection
'   POST /api/heavy/jobs/agregar-uno    — agrega un job explicito
'   GET  /api/heavy/jobs                — lista jobs en la cola
'   POST /api/heavy/jobs/limpiar        — vacia la cola (si no hay ejecucion)
'   POST /api/heavy/ejecutar            — arranca la ejecucion (body:{"concurrencia":2})
'   POST /api/heavy/detener             — cancela ejecucion
'   GET  /api/heavy/sesiones            — sesiones persistidas en BD
'   GET  /api/heavy/sesion/{id}         — una sesion + KPIs
'   GET  /api/heavy/sesion/{id}/ops     — operaciones de la sesion
'   DELETE /api/heavy/sesion/{id}       — borra sesion + sus operaciones
' ============================================================
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Threading
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports Npgsql
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas
Imports WpfDerivClientVB.WpfDerivClientVB.Servidor

Namespace WpfDerivClientVB.Backtesting

    Public Class HeavyApiServer

        Private _listener As HttpListener
        Private _cts As CancellationTokenSource

        Public ReadOnly Property Port As Integer
        Public ReadOnly Property IsRunning As Boolean
            Get
                Return _listener IsNot Nothing AndAlso _listener.IsListening
            End Get
        End Property

        ' Inyecciones desde la ventana
        Public Property Queue As HeavyTestQueue
        Public Property Repo As BacktestRepository
        Public Property ConnectionString As String
        Public Property UiDispatcher As Dispatcher
        Public Property LogAction As Action(Of String)

        ''' <summary>
        ''' Callback que la ventana implementa para arrancar la ejecucion. Se llama
        ''' desde el thread del dispatcher UI. Recibe la concurrencia.
        ''' </summary>
        Public Property StartEjecucion As Func(Of Integer, Task)

        ' Servicio de descarga historica (standalone: Deriv WS publico + Postgres).
        ' Lazy-init para no abrir recursos si nadie descarga.
        Private _descarga As DescargaService = Nothing
        Private ReadOnly _descargaLock As New Object()
        Private ReadOnly Property Descarga As DescargaService
            Get
                SyncLock _descargaLock
                    If _descarga Is Nothing Then
                        _descarga = New DescargaService(New CandleRepository(ConnectionString))
                    End If
                    Return _descarga
                End SyncLock
            End Get
        End Property

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
            Log(String.Format("[HeavyApi] Servidor iniciado en http://localhost:{0}/", _Port))
        End Sub

        Public Sub [Stop]()
            _cts?.Cancel()
            Try
                _listener?.Stop()
                _listener?.Close()
            Catch
            End Try
            _listener = Nothing
            Log("[HeavyApi] Servidor detenido.")
        End Sub

        Private Sub Log(msg As String)
            If LogAction IsNot Nothing Then LogAction.Invoke(msg)
        End Sub

        ' ===================================================
        '  BUCLE PRINCIPAL
        ' ===================================================
        Private Async Function ListenLoop() As Task
            Do While _listener IsNot Nothing AndAlso Not _cts.IsCancellationRequested
                Try
                    If Not _listener.IsListening Then Exit Do
                    Dim ctx = Await _listener.GetContextAsync()
#Disable Warning BC42358
                    Task.Run(Function() HandleRequest(ctx))
#Enable Warning BC42358
                Catch ex As ObjectDisposedException
                    Exit Do
                Catch ex As HttpListenerException
                    If _cts.IsCancellationRequested Then Exit Do
                    Log("[HeavyApi] HttpListenerException: " & ex.Message)
                Catch ex As Exception
                    If _cts.IsCancellationRequested Then Exit Do
                    Log("[HeavyApi] Error: " & ex.Message)
                End Try
            Loop
        End Function

        ' ===================================================
        '  DISPATCHER DE RUTAS
        ' ===================================================
        Private Async Function HandleRequest(ctx As HttpListenerContext) As Task
            Dim req = ctx.Request
            Dim res = ctx.Response
            res.ContentType = "application/json; charset=utf-8"
            ' Sin CORS: solo clientes locales con token (ver Infrastructure/AgentAuth.vb)

            ' --- Autenticacion: header X-Agent-Token obligatorio ---
            Dim motivoRechazo = AgentAuth.Validar(req)
            If motivoRechazo <> "" Then
                Log("[HeavyApi] RECHAZADO " & req.HttpMethod & " " & req.Url.AbsolutePath & ": " & motivoRechazo)
                Await WriteJson(res, 401, New JObject From {{"ok", False}, {"error", motivoRechazo}})
                Return
            End If

            Dim path = req.Url.AbsolutePath.TrimEnd("/"c).ToLower()
            Dim method = req.HttpMethod.ToUpper()
            Dim qs = req.QueryString

            Log(String.Format("[{0}] {1} {2}", DateTime.Now.ToString("HH:mm:ss"), method, path))

            Try
                ' Body POST/DELETE
                Dim body As JObject = Nothing
                If method = "POST" OrElse method = "DELETE" OrElse method = "PUT" Then
                    Using sr As New StreamReader(req.InputStream, Encoding.UTF8)
                        Dim raw = Await sr.ReadToEndAsync()
                        If Not String.IsNullOrWhiteSpace(raw) Then
                            Try
                                body = JObject.Parse(raw)
                            Catch
                                body = Nothing
                            End Try
                        End If
                    End Using
                End If

                ' --- Routing ---
                If path = "/api/heavy/help" Then
                    Await WriteJson(res, 200, BuildHelp())
                    Return
                End If

                If path = "/api/heavy/status" Then
                    Await WriteJson(res, 200, BuildStatus())
                    Return
                End If

                ' --- Gestor de riesgo (Infrastructure/RiskManager.vb) ---
                If path = "/api/heavy/riesgo/estado" AndAlso method = "GET" Then
                    Await WriteJson(res, 200, RiskManager.GetEstado())
                    Return
                End If
                If path = "/api/heavy/riesgo/parar" AndAlso method = "POST" Then
                    RiskManager.ActivarParada(If(body?("motivo")?.ToString(), "Parada por API"))
                    Await WriteJson(res, 200, RiskManager.GetEstado())
                    Return
                End If
                If path = "/api/heavy/riesgo/reanudar" AndAlso method = "POST" Then
                    RiskManager.DesactivarParada()
                    Await WriteJson(res, 200, RiskManager.GetEstado())
                    Return
                End If

                If path = "/api/heavy/activos" Then
                    Await WriteJson(res, 200, Await BuildActivos())
                    Return
                End If

                If path = "/api/heavy/estrategias" Then
                    Await WriteJson(res, 200, BuildEstrategias())
                    Return
                End If

                If path = "/api/heavy/timeframes" Then
                    Await WriteJson(res, 200, New JObject From {
                        {"ok", True},
                        {"timeframes", New JArray From {"1m", "5m", "15m", "30m", "1h", "4h", "1d"}}
                    })
                    Return
                End If

                If path = "/api/heavy/jobs" Then
                    Await WriteJson(res, 200, BuildJobs())
                    Return
                End If

                If path = "/api/heavy/jobs/agregar" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, AgregarJobs(body))
                    Return
                End If

                If path = "/api/heavy/jobs/agregar-uno" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, AgregarJobUnico(body))
                    Return
                End If

                If path = "/api/heavy/jobs/limpiar" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, LimpiarCola())
                    Return
                End If

                If path = "/api/heavy/ejecutar" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await EjecutarCola(body))
                    Return
                End If

                If path = "/api/heavy/detener" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, DetenerCola())
                    Return
                End If

                ' ── MODO AGENTE: control de estrategias LIVE ─────────────
                If path = "/api/heavy/live/estado" Then
                    Await WriteJson(res, 200, LiveStrategiesController.GetInstance().GetEstadoGlobal())
                    Return
                End If
                If path = "/api/heavy/live/iniciar" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await LiveIniciar(body))
                    Return
                End If
                If path = "/api/heavy/live/detener" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await LiveDetener(body))
                    Return
                End If
                If path = "/api/heavy/live/pausar" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, LivePausar(body))
                    Return
                End If
                If path = "/api/heavy/live/reanudar-bd" AndAlso method = "POST" Then
                    Dim n = Await LiveStrategiesController.GetInstance().ReanudarActivasAsync()
                    Await WriteJson(res, 200, New JObject From {{"ok", True}, {"reanudadas", n}})
                    Return
                End If

                ' ── MODO AGENTE: cliente SignalR ─────────────────────────
                If path = "/api/heavy/signalr/estado" Then
                    Dim sr = SignalRClientService.GetInstance()
                    Await WriteJson(res, 200, New JObject From {
                        {"ok", True}, {"estado", sr.Estado}, {"conectado", sr.EstaConectado},
                        {"url", sr.ServerUrl}, {"client_id", sr.ClientId}, {"hub", sr.HubName}
                    })
                    Return
                End If
                If path = "/api/heavy/signalr/conectar" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await SignalRConectar(body))
                    Return
                End If
                If path = "/api/heavy/signalr/desconectar" AndAlso method = "POST" Then
                    SignalRClientService.GetInstance().Desconectar()
                    Await WriteJson(res, 200, New JObject From {{"ok", True}})
                    Return
                End If
                If path = "/api/heavy/signalr/enviar" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await SignalREnviar(body))
                    Return
                End If

                ' ── MODO AGENTE: CRUD de estrategias JSON ────────────────
                If path = "/api/heavy/estrategias/guardar" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, EstrategiaGuardar(body))
                    Return
                End If
                If path.StartsWith("/api/heavy/estrategias/") Then
                    Dim nombreUrl = path.Substring("/api/heavy/estrategias/".Length)
                    If nombreUrl <> "" AndAlso Not nombreUrl.Contains("/") Then
                        If method = "GET" Then
                            Await WriteJson(res, 200, EstrategiaLeer(nombreUrl))
                            Return
                        End If
                        If method = "DELETE" Then
                            Await WriteJson(res, 200, EstrategiaEliminar(nombreUrl))
                            Return
                        End If
                    End If
                End If

                ' ── MODO AGENTE: trading (WS Deriv) ──────────────────────
                If path = "/api/heavy/trading/estado" Then
                    Dim svc = TradingServiceLocator.Current
                    If svc Is Nothing Then
                        Await WriteJson(res, 200, ErrJson("TradingService no registrado. Abre la ventana de trading: POST /api/heavy/app/ventana {""nombre"":""trading""}"))
                    Else
                        Await WriteJson(res, 200, svc.GetStatus())
                    End If
                    Return
                End If
                If path = "/api/heavy/trading/conectar" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await TradingConectar(body))
                    Return
                End If

                ' Consulta de solo lectura por la conexion autenticada (lista blanca en el servicio)
                If path = "/api/heavy/trading/consulta" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await DerivTradingService.Instance.ConsultarAsync(body))
                    Return
                End If

                ' Validacion fuera de muestra de una estrategia (no guarda nada)
                If path = "/api/heavy/validar" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await ValidarEstrategiaApi(body))
                    Return
                End If

                ' ── MODO AGENTE: abrir ventanas ──────────────────────────
                If path = "/api/heavy/app/ventana" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, AbrirVentana(body))
                    Return
                End If

                ' ── MODO AGENTE: descarga historica (Deriv → Postgres) ───
                If path = "/api/heavy/historico/descargar" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await HistoricoDescargar(body))
                    Return
                End If
                If path = "/api/heavy/historico/estado" Then
                    Await WriteJson(res, 200, HistoricoEstado())
                    Return
                End If
                If path = "/api/heavy/historico/detener" AndAlso method = "POST" Then
                    Descarga.Detener()
                    Await WriteJson(res, 200, New JObject From {{"ok", True}, {"mensaje", "Detencion solicitada."}})
                    Return
                End If

                If path = "/api/heavy/sesiones" Then
                    Await WriteJson(res, 200, Await BuildSesiones(qs))
                    Return
                End If

                ' /api/heavy/sesion/{id}            (sesion + kpis)
                ' /api/heavy/sesion/{id}/ops        (lista de operaciones)
                ' DELETE /api/heavy/sesion/{id}     (borra)
                If path.StartsWith("/api/heavy/sesion/") Then
                    Dim parts = path.Substring("/api/heavy/sesion/".Length).Split("/"c)
                    Dim sid As Integer = 0
                    If parts.Length >= 1 AndAlso Integer.TryParse(parts(0), sid) Then
                        If parts.Length = 2 AndAlso parts(1) = "ops" AndAlso method = "GET" Then
                            Await WriteJson(res, 200, Await BuildSesionOps(sid))
                            Return
                        End If
                        If parts.Length = 1 AndAlso method = "GET" Then
                            Await WriteJson(res, 200, Await BuildSesionDetalle(sid))
                            Return
                        End If
                        If parts.Length = 1 AndAlso method = "DELETE" Then
                            Await WriteJson(res, 200, Await EliminarSesion(sid))
                            Return
                        End If
                    End If
                End If

                Await WriteJson(res, 404, ErrJson("Endpoint no encontrado: " & method & " " & path))
                Return
            Catch ex As Exception
                Log("[HeavyApi] EXC: " & ex.Message)
            End Try

            ' Fuera del catch (await no puede usarse dentro de Catch en VB)
            Try
                Await WriteJson(res, 500, ErrJson("Excepcion en handler"))
            Catch
            End Try
        End Function

        ' ===================================================
        '  HANDLERS
        ' ===================================================

        Private Function BuildHelp() As JObject
            Dim ej As New JObject From {
                {"activos", New JArray From {"R_100"}},
                {"estrategias", New JArray From {"BollingerRsiReversal"}},
                {"timeframes", New JArray From {"1m", "5m"}},
                {"confianzas", New JArray From {55, 65, 75}},
                {"payouts", New JArray From {85, 90}},
                {"duraciones", New JArray From {3, 5}},
                {"capitales", New JArray From {100}},
                {"desde_epoch", 1700000000L},
                {"hasta_epoch", 1730000000L}
            }
            Dim endpoints As New JArray From {
                "GET  /api/heavy/help",
                "GET  /api/heavy/status",
                "GET  /api/heavy/activos",
                "GET  /api/heavy/estrategias",
                "GET  /api/heavy/timeframes",
                "GET  /api/heavy/jobs",
                "POST /api/heavy/jobs/agregar     (body: HeavySelection)",
                "POST /api/heavy/jobs/agregar-uno (body: 1 job explicito)",
                "POST /api/heavy/jobs/limpiar",
                "POST /api/heavy/ejecutar         (body: {concurrencia:2})",
                "POST /api/heavy/detener",
                "GET  /api/heavy/sesiones?limite=100",
                "GET  /api/heavy/sesion/{id}",
                "GET  /api/heavy/sesion/{id}/ops",
                "DELETE /api/heavy/sesion/{id}",
                "GET  /api/heavy/live/estado",
                "POST /api/heavy/live/iniciar     (body: {estrategia,symbol,timeframe,confianza,payout,duracion,monto})",
                "POST /api/heavy/live/detener     (body: {sesion_id})",
                "POST /api/heavy/live/pausar      (body: {sesion_id, pausar:true|false})",
                "POST /api/heavy/live/reanudar-bd",
                "GET  /api/heavy/signalr/estado",
                "POST /api/heavy/signalr/conectar (body opcional: {url, client_id, hub} — default config)",
                "POST /api/heavy/signalr/desconectar",
                "POST /api/heavy/signalr/enviar   (body: {target, instruccion, parametros})",
                "GET  /api/heavy/estrategias/{nombre}",
                "POST /api/heavy/estrategias/guardar (body: definicion JSON completa)",
                "DELETE /api/heavy/estrategias/{nombre}",
                "GET  /api/heavy/trading/estado",
                "POST /api/heavy/trading/conectar (body: {demo:true, cliente:''})  demo:false bloqueado salvo PermitirRealRemoto",
                "POST /api/heavy/app/ventana      (body: {nombre: trading|servidorlive|heavy})",
                "GET  /api/heavy/riesgo/estado",
                "POST /api/heavy/riesgo/parar     (body: {motivo})",
                "POST /api/heavy/riesgo/reanudar",
                "POST /api/heavy/historico/descargar (body: {symbol, timeframe, desde:YYYY-MM-DD, hasta:YYYY-MM-DD})",
                "GET  /api/heavy/historico/estado",
                "POST /api/heavy/historico/detener",
                "(todas requieren header X-Agent-Token)"
            }
            Return New JObject From {
                {"ok", True},
                {"servicio", "Heavy Strategy Tester"},
                {"puerto", _Port},
                {"endpoints", endpoints},
                {"ejemplo_agregar", ej},
                {"notas", "Las fechas pueden venir como 'desde_epoch'/'hasta_epoch' (segundos UNIX) o " &
                          "'desde_iso'/'hasta_iso' (ISO 8601 UTC). Si una lista de rangos esta vacia se usa default."}
            }
        End Function

        Private Function BuildStatus() As JObject
            Dim total = 0, pendientes = 0, ejec = 0, ok = 0, err = 0, canc = 0
            If Queue IsNot Nothing Then
                For Each j In Queue.Jobs.ToList()
                    total += 1
                    Select Case j.Estado
                        Case HeavyJobEstado.Pendiente : pendientes += 1
                        Case HeavyJobEstado.Ejecutando : ejec += 1
                        Case HeavyJobEstado.Completado : ok += 1
                        Case HeavyJobEstado.Error_ : err += 1
                        Case HeavyJobEstado.Cancelado : canc += 1
                    End Select
                Next
            End If
            Return New JObject From {
                {"ok", True},
                {"servicio", "Heavy Strategy Tester"},
                {"puerto", _Port},
                {"en_curso", If(Queue IsNot Nothing AndAlso Queue.EnCurso, True, False)},
                {"jobs_total", total},
                {"jobs_pendientes", pendientes},
                {"jobs_ejecutando", ejec},
                {"jobs_completados", ok},
                {"jobs_error", err},
                {"jobs_cancelados", canc}
            }
        End Function

        Private Async Function BuildActivos() As Task(Of JObject)
            Dim activos As New List(Of String)()
            Try
                Using conn As New NpgsqlConnection(ConnectionString)
                    Await conn.OpenAsync()
                    Using cmd As New NpgsqlCommand("SELECT DISTINCT symbol FROM public.candles ORDER BY symbol;", conn)
                        Using r = Await cmd.ExecuteReaderAsync()
                            Do While Await r.ReadAsync()
                                activos.Add(r.GetString(0))
                            Loop
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                Return ErrJson("BD: " & ex.Message)
            End Try
            Return New JObject From {
                {"ok", True}, {"total", activos.Count},
                {"activos", JArray.FromObject(activos)}
            }
        End Function

        Private Function BuildEstrategias() As JObject
            Try
                EstrategiasDinamicasRegistry.Inicializar()
                Dim lista = EstrategiasDinamicasRegistry.GetAll()
                Dim arr As New JArray()
                For Each e In lista
                    arr.Add(New JObject From {
                        {"nombre", e.Nombre},
                        {"version", If(e.Definicion IsNot Nothing, e.Definicion.Version, "")},
                        {"descripcion", If(e.Definicion IsNot Nothing AndAlso e.Definicion.Descripcion IsNot Nothing,
                                           e.Definicion.Descripcion, "")}
                    })
                Next
                Return New JObject From {
                    {"ok", True}, {"total", lista.Count}, {"estrategias", arr}
                }
            Catch ex As Exception
                Return ErrJson("Estrategias: " & ex.Message)
            End Try
        End Function

        Private Function BuildJobs() As JObject
            Dim arr As New JArray()
            If Queue IsNot Nothing Then
                For Each j In Queue.Jobs.ToList()
                    arr.Add(New JObject From {
                        {"id", j.Id},
                        {"estrategia", j.Estrategia},
                        {"symbol", j.Symbol},
                        {"timeframe", j.Timeframe},
                        {"confianza", j.Confianza},
                        {"payout", j.Payout},
                        {"duracion", j.Duracion},
                        {"capital", j.Capital},
                        {"desde_epoch", j.DesdeEpoch},
                        {"hasta_epoch", j.HastaEpoch},
                        {"estado", j.EstadoStr},
                        {"progreso", j.Progreso},
                        {"velas", j.VelasAnalizadas},
                        {"velas_total", j.TotalVelas},
                        {"ganadas", j.GanadasParcial},
                        {"perdidas", j.PerdidasParcial},
                        {"fecha_inicio", If(j.FechaInicio = DateTime.MinValue, Nothing, j.FechaInicio.ToString("yyyy-MM-dd HH:mm:ss"))},
                        {"fecha_fin", If(j.FechaFin = DateTime.MinValue, Nothing, j.FechaFin.ToString("yyyy-MM-dd HH:mm:ss"))},
                        {"error", j.MensajeError}
                    })
                Next
            End If
            Return New JObject From {{"ok", True}, {"total", arr.Count}, {"jobs", arr}}
        End Function

        Private Function AgregarJobs(body As JObject) As JObject
            If Queue Is Nothing Then Return ErrJson("Queue no inicializada.")
            If body Is Nothing Then Return ErrJson("Body JSON requerido. Llamar GET /api/heavy/help para ver ejemplo.")

            Try
                Dim sel As New HeavySelection()
                sel.Activos = JsonStringList(body, "activos")
                sel.Estrategias = JsonStringList(body, "estrategias")
                sel.Timeframes = JsonStringList(body, "timeframes")
                sel.Confianzas = JsonIntList(body, "confianzas")
                sel.Payouts = JsonDecimalList(body, "payouts")
                sel.Duraciones = JsonIntList(body, "duraciones")
                sel.Capitales = JsonDecimalList(body, "capitales")
                ParseDesdeHasta(body, sel)

                If sel.Activos.Count = 0 OrElse sel.Estrategias.Count = 0 OrElse sel.Timeframes.Count = 0 Then
                    Return ErrJson("'activos', 'estrategias' y 'timeframes' son obligatorios y no pueden estar vacios.")
                End If

                ' La cola es ObservableCollection → modificar desde el dispatcher UI
                Dim agregados As Integer = 0
                UiDispatcher.Invoke(Sub()
                                        agregados = Queue.GenerarCombinaciones(sel)
                                    End Sub)

                Return New JObject From {
                    {"ok", True},
                    {"agregados", agregados},
                    {"jobs_total", Queue.Jobs.Count},
                    {"combinaciones_pedidas", sel.TotalCombinaciones}
                }
            Catch ex As Exception
                Return ErrJson("Error: " & ex.Message)
            End Try
        End Function

        Private Function AgregarJobUnico(body As JObject) As JObject
            If Queue Is Nothing Then Return ErrJson("Queue no inicializada.")
            If body Is Nothing Then Return ErrJson("Body JSON requerido.")
            Try
                Dim job As New HeavyTestJob With {
                    .Estrategia = If(body("estrategia")?.ToString(), ""),
                    .Symbol = If(body("symbol")?.ToString(), ""),
                    .Timeframe = If(body("timeframe")?.ToString(), ""),
                    .Confianza = If(body("confianza") IsNot Nothing, CInt(body("confianza")), 55),
                    .Payout = If(body("payout") IsNot Nothing, CDec(body("payout")), 85D),
                    .Duracion = If(body("duracion") IsNot Nothing, CInt(body("duracion")), 5),
                    .Capital = If(body("capital") IsNot Nothing, CDec(body("capital")), 100D),
                    .Estado = HeavyJobEstado.Pendiente
                }
                ' Fechas
                Dim selTmp As New HeavySelection()
                ParseDesdeHasta(body, selTmp)
                job.DesdeEpoch = selTmp.DesdeEpoch
                job.HastaEpoch = selTmp.HastaEpoch

                If String.IsNullOrEmpty(job.Estrategia) OrElse String.IsNullOrEmpty(job.Symbol) OrElse
                   String.IsNullOrEmpty(job.Timeframe) Then
                    Return ErrJson("'estrategia', 'symbol', 'timeframe' son obligatorios.")
                End If

                UiDispatcher.Invoke(Sub() Queue.Jobs.Add(job))

                Return New JObject From {
                    {"ok", True},
                    {"id", job.Id},
                    {"jobs_total", Queue.Jobs.Count}
                }
            Catch ex As Exception
                Return ErrJson("Error: " & ex.Message)
            End Try
        End Function

        Private Function LimpiarCola() As JObject
            If Queue Is Nothing Then Return ErrJson("Queue no inicializada.")
            If Queue.EnCurso Then Return ErrJson("No se puede limpiar mientras hay ejecucion en curso.")
            UiDispatcher.Invoke(Sub() Queue.Limpiar())
            Return New JObject From {{"ok", True}, {"jobs_total", 0}}
        End Function

        Private Async Function EjecutarCola(body As JObject) As Task(Of JObject)
            If Queue Is Nothing Then Return ErrJson("Queue no inicializada.")
            If Queue.EnCurso Then Return ErrJson("Ya hay una ejecucion en curso.")
            If StartEjecucion Is Nothing Then Return ErrJson("Callback de ejecucion no configurado.")
            Dim conc As Integer = 2
            If body IsNot Nothing AndAlso body("concurrencia") IsNot Nothing Then
                Integer.TryParse(body("concurrencia").ToString(), conc)
            End If
            If conc < 1 Then conc = 1
            If conc > 8 Then conc = 8

            ' Disparar la ejecucion sin awaitearla — el cliente puede pollear /status
            Dim cArg = conc
            UiDispatcher.BeginInvoke(Sub()
                                         Try
                                             Dim _ignore = StartEjecucion.Invoke(cArg)
                                         Catch ex As Exception
                                             Log("[HeavyApi] StartEjecucion: " & ex.Message)
                                         End Try
                                     End Sub)
            Await Task.Delay(50)
            Return New JObject From {
                {"ok", True},
                {"mensaje", "Ejecucion iniciada en background. Pollear /api/heavy/status."},
                {"concurrencia", conc}
            }
        End Function

        Private Function DetenerCola() As JObject
            If Queue Is Nothing Then Return ErrJson("Queue no inicializada.")
            If Not Queue.EnCurso Then Return ErrJson("No hay ejecucion en curso.")
            UiDispatcher.Invoke(Sub() Queue.Cancelar())
            Return New JObject From {{"ok", True}, {"mensaje", "Cancelacion solicitada."}}
        End Function

        Private Async Function BuildSesiones(qs As Specialized.NameValueCollection) As Task(Of JObject)
            If Repo Is Nothing Then Return ErrJson("Repo no inicializado.")
            Dim limite As Integer = 100
            Integer.TryParse(If(qs("limite"), "100"), limite)
            If limite < 1 Then limite = 1
            If limite > 5000 Then limite = 5000

            Dim sesiones = Await Repo.GetSesionesAsync(limite)
            Dim arr As New JArray()
            For Each s In sesiones
                arr.Add(SesionToJson(s))
            Next
            Return New JObject From {{"ok", True}, {"total", arr.Count}, {"sesiones", arr}}
        End Function

        Private Async Function BuildSesionDetalle(sid As Integer) As Task(Of JObject)
            If Repo Is Nothing Then Return ErrJson("Repo no inicializado.")
            Dim s = Await Repo.GetSesionPorIdAsync(sid)
            If s Is Nothing Then Return ErrJson("Sesion #" & sid & " no encontrada.")
            Dim total = Await Repo.ContarOperacionesAsync(sid)
            Dim j = SesionToJson(s)
            j("total_ops_bd") = total
            Return New JObject From {{"ok", True}, {"sesion", j}}
        End Function

        Private Async Function BuildSesionOps(sid As Integer) As Task(Of JObject)
            If Repo Is Nothing Then Return ErrJson("Repo no inicializado.")
            Dim ops = Await Repo.GetOperacionesPorSesionAsync(sid)
            Dim arr As New JArray()
            For Each o In ops
                arr.Add(New JObject From {
                    {"epoch_entrada", o.EpochEntrada},
                    {"epoch_salida", o.EpochSalida},
                    {"tipo", o.Tipo},
                    {"precio_entrada", o.PrecioEntrada},
                    {"precio_salida", o.PrecioSalida},
                    {"monto", o.Monto},
                    {"payout_pct", o.PayoutPct},
                    {"estado", o.Estado},
                    {"profit_loss", o.ProfitLoss},
                    {"balance_post", o.BalancePost},
                    {"confianza", o.Confianza}
                })
            Next
            Return New JObject From {{"ok", True}, {"sesion_id", sid}, {"total", arr.Count}, {"operaciones", arr}}
        End Function

        Private Async Function EliminarSesion(sid As Integer) As Task(Of JObject)
            If Repo Is Nothing Then Return ErrJson("Repo no inicializado.")
            Try
                Dim res = Await Repo.EliminarSesionAsync(sid)
                Return New JObject From {
                    {"ok", True},
                    {"sesion_id", sid},
                    {"sesion_borrada", res.SesionBorrada},
                    {"operaciones_borradas", res.OpsBorradas}
                }
            Catch ex As Exception
                Return ErrJson("Error eliminando: " & ex.Message)
            End Try
        End Function

        ' ===================================================
        '  HANDLERS MODO AGENTE
        ' ===================================================

        Private Async Function LiveIniciar(body As JObject) As Task(Of JObject)
            If body Is Nothing Then Return ErrJson("Body requerido: {estrategia, symbol, timeframe, confianza, payout, duracion, monto}")
            Try
                Dim nombre = If(body("estrategia")?.ToString(), "")
                Dim symbol = If(body("symbol")?.ToString(), "")
                Dim tf = If(body("timeframe")?.ToString(), "")
                If nombre = "" OrElse symbol = "" OrElse tf = "" Then
                    Return ErrJson("estrategia, symbol y timeframe son obligatorios")
                End If
                Dim conf = If(body("confianza") IsNot Nothing, CInt(body("confianza")), 55)
                Dim pay = If(body("payout") IsNot Nothing, CDec(body("payout")), 85D)
                Dim dur = If(body("duracion") IsNot Nothing, CInt(body("duracion")), 5)
                Dim monto = If(body("monto") IsNot Nothing, CDec(body("monto")), 1D)
                Dim sid = Await LiveStrategiesController.GetInstance().IniciarAsync(
                    nombre, symbol, tf, conf, pay, dur, monto)
                Return New JObject From {{"ok", True}, {"sesion_id", sid}}
            Catch ex As Exception
                Return ErrJson(ex.Message)
            End Try
        End Function

        Private Async Function LiveDetener(body As JObject) As Task(Of JObject)
            Dim sid = If(body?("sesion_id") IsNot Nothing, CInt(body("sesion_id")), 0)
            If sid <= 0 Then Return ErrJson("sesion_id requerido")
            Dim ok = Await LiveStrategiesController.GetInstance().DetenerAsync(sid)
            Return New JObject From {{"ok", ok}, {"sesion_id", sid}}
        End Function

        Private Function LivePausar(body As JObject) As JObject
            Dim sid = If(body?("sesion_id") IsNot Nothing, CInt(body("sesion_id")), 0)
            If sid <= 0 Then Return ErrJson("sesion_id requerido")
            Dim pausar = If(body("pausar") IsNot Nothing, CBool(body("pausar")), True)
            Dim ok = LiveStrategiesController.GetInstance().PausarReanudar(sid, pausar)
            Return New JObject From {{"ok", ok}, {"sesion_id", sid}, {"pausado", pausar}}
        End Function

        Private Async Function SignalRConectar(body As JObject) As Task(Of JObject)
            Try
                Dim cfg = ConfigManager.LoadDbConfig()
                Dim url = If(body?("url")?.ToString(), If(cfg.SignalRUrl, ""))
                Dim cid = If(body?("client_id")?.ToString(),
                             If(String.IsNullOrWhiteSpace(cfg.SignalRClientId), Environment.MachineName, cfg.SignalRClientId))
                Dim hub = If(body?("hub")?.ToString(),
                             If(String.IsNullOrWhiteSpace(cfg.SignalRHubName), SignalRClientService.HUB_NAME_DEFAULT, cfg.SignalRHubName))
                If String.IsNullOrWhiteSpace(url) Then Return ErrJson("No hay URL (ni en body ni en config).")

                Await SignalRClientService.GetInstance().ConectarAsync(url, cid, hub)
                Dim sr = SignalRClientService.GetInstance()
                Return New JObject From {
                    {"ok", sr.EstaConectado}, {"estado", sr.Estado},
                    {"url", url}, {"client_id", cid}, {"hub", hub}
                }
            Catch ex As Exception
                Return ErrJson(ex.Message)
            End Try
        End Function

        Private Async Function SignalREnviar(body As JObject) As Task(Of JObject)
            If body Is Nothing Then Return ErrJson("Body requerido: {target, instruccion, parametros}")
            Dim sr = SignalRClientService.GetInstance()
            If Not sr.EstaConectado Then Return ErrJson("SignalR no conectado. POST /api/heavy/signalr/conectar primero.")
            Dim target = If(body("target")?.ToString(), sr.ClientId)
            Dim instruccion = If(body("instruccion")?.ToString(), "")
            If instruccion = "" Then Return ErrJson("instruccion requerida")
            Dim parametros = If(body("parametros") IsNot Nothing,
                                 If(TypeOf body("parametros") Is JObject,
                                    body("parametros").ToString(Formatting.None),
                                    body("parametros").ToString()), "{}")
            Dim ok = Await sr.EnviarInstruccionAsync(target, instruccion, parametros)
            Return New JObject From {{"ok", ok}, {"target", target}, {"instruccion", instruccion}}
        End Function

        Private Function EstrategiaGuardar(body As JObject) As JObject
            If body Is Nothing Then Return ErrJson("Body = definicion completa de la estrategia (JSON con 'nombre', 'indicadores', 'senal_call', ...)")
            Try
                ' 1) Validar deserializacion + compilacion (indicadores y condiciones)
                Dim def = JsonConvert.DeserializeObject(Of EstrategiaDinamicaDef)(body.ToString())
                If def Is Nothing OrElse String.IsNullOrWhiteSpace(def.Nombre) Then
                    Return ErrJson("La definicion necesita al menos 'nombre'.")
                End If
                Dim testCompile As New EstrategiaJson(def)   ' lanza si hay indicador/condicion invalida

                ' 2) Sanitizar nombre de archivo
                Dim safe As New Text.StringBuilder()
                For Each ch In def.Nombre
                    If Char.IsLetterOrDigit(ch) OrElse ch = "_"c OrElse ch = "-"c Then safe.Append(ch)
                Next
                If safe.Length = 0 Then Return ErrJson("Nombre invalido.")

                ' 3) Escribir al directorio del registry (hot-reload automatico)
                Dim dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EstrategiasDinamicas", "estrategias")
                If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
                Dim archivo = Path.Combine(dir, safe.ToString() & ".json")
                Dim existia = File.Exists(archivo)
                File.WriteAllText(archivo, body.ToString(Formatting.Indented))

                Return New JObject From {
                    {"ok", True}, {"nombre", def.Nombre},
                    {"archivo", archivo}, {"sobrescrita", existia},
                    {"mensaje", "Guardada y compilada OK. El registry la recarga automaticamente."}
                }
            Catch ex As Exception
                Return ErrJson("Validacion fallo: " & ex.Message)
            End Try
        End Function

        Private Function EstrategiaLeer(nombreUrl As String) As JObject
            Try
                EstrategiasDinamicasRegistry.Inicializar()
                Dim est = EstrategiasDinamicasRegistry.GetAll().
                    FirstOrDefault(Function(e) e.Nombre.ToLowerInvariant() = nombreUrl.ToLowerInvariant())
                If est Is Nothing Then Return ErrJson("Estrategia no encontrada: " & nombreUrl)
                Dim archivo = est.Definicion.ArchivoOrigen
                If String.IsNullOrEmpty(archivo) OrElse Not File.Exists(archivo) Then
                    Return ErrJson("Archivo origen no disponible para " & est.Nombre)
                End If
                Return New JObject From {
                    {"ok", True}, {"nombre", est.Nombre}, {"archivo", archivo},
                    {"definicion", JObject.Parse(File.ReadAllText(archivo))}
                }
            Catch ex As Exception
                Return ErrJson(ex.Message)
            End Try
        End Function

        Private Function EstrategiaEliminar(nombreUrl As String) As JObject
            Try
                EstrategiasDinamicasRegistry.Inicializar()
                Dim est = EstrategiasDinamicasRegistry.GetAll().
                    FirstOrDefault(Function(e) e.Nombre.ToLowerInvariant() = nombreUrl.ToLowerInvariant())
                If est Is Nothing Then Return ErrJson("Estrategia no encontrada: " & nombreUrl)
                Dim archivo = est.Definicion.ArchivoOrigen
                If String.IsNullOrEmpty(archivo) OrElse Not File.Exists(archivo) Then
                    Return ErrJson("Archivo origen no disponible.")
                End If
                File.Delete(archivo)
                Return New JObject From {{"ok", True}, {"nombre", est.Nombre}, {"archivo_eliminado", archivo}}
            Catch ex As Exception
                Return ErrJson(ex.Message)
            End Try
        End Function

        Private Async Function TradingConectar(body As JObject) As Task(Of JObject)
            Dim demo = If(body?("demo") IsNot Nothing, CBool(body("demo")), True)
            Dim cliente = If(body?("cliente")?.ToString(), "")
            ' Seguridad (primera comprobacion): la cuenta REAL no se conecta por API
            ' salvo permiso local explicito en db_config.json.
            If Not demo AndAlso Not ConfigManager.LoadDbConfig().PermitirRealRemoto Then
                Log("[HeavyApi] BLOQUEADO: intento de conectar cuenta REAL por API.")
                Return ErrJson("Conectar la cuenta REAL por API esta bloqueado. Para permitirlo, pon ""PermitirRealRemoto"": true en db_config.json.")
            End If
            Dim svc = TradingServiceLocator.Current
            If svc Is Nothing Then
                Return ErrJson("TradingService no registrado. Abre la ventana de trading primero: POST /api/heavy/app/ventana {""nombre"":""trading""}")
            End If
            Try
                Return Await svc.ConectarTradingAsync(demo, cliente)
            Catch ex As Exception
                Return ErrJson(ex.Message)
            End Try
        End Function

        Private Async Function HistoricoDescargar(body As JObject) As Task(Of JObject)
            If body Is Nothing Then Return ErrJson("Body: {symbol, timeframe, desde:YYYY-MM-DD, hasta:YYYY-MM-DD}")
            Dim symbol = If(body("symbol")?.ToString(), "")
            Dim tf = If(body("timeframe")?.ToString(), "")
            Dim desdeStr = If(body("desde")?.ToString(), "")
            Dim hastaStr = If(body("hasta")?.ToString(), DateTime.UtcNow.ToString("yyyy-MM-dd"))
            If symbol = "" OrElse tf = "" OrElse desdeStr = "" Then
                Return ErrJson("symbol, timeframe y desde son obligatorios")
            End If
            Dim fromD, toD As DateTime
            If Not DateTime.TryParse(desdeStr, fromD) Then Return ErrJson("Fecha 'desde' invalida (use YYYY-MM-DD)")
            If Not DateTime.TryParse(hastaStr, toD) Then Return ErrJson("Fecha 'hasta' invalida (use YYYY-MM-DD)")

            If Descarga.EstaDescargando Then
                Return New JObject From {
                    {"ok", False}, {"error", "Ya hay una descarga en curso."},
                    {"estado", EstadoDescargaJson(Descarga.ObtenerEstado())}
                }
            End If

            ' Lanzar en background — el cliente pollea /api/heavy/historico/estado
            Dim _t = Task.Run(Async Function()
                                  Try
                                      Await Descarga.DescargarAsync(symbol, tf, fromD, toD)
                                  Catch ex As Exception
                                      Log("[Historico] Error: " & ex.Message)
                                  End Try
                              End Function)
            Await Task.Delay(300)
            Return New JObject From {
                {"ok", True},
                {"mensaje", String.Format("Descarga iniciada: {0} {1} {2}→{3}. Pollear /api/heavy/historico/estado.",
                                           symbol, tf, fromD.ToString("yyyy-MM-dd"), toD.ToString("yyyy-MM-dd"))}
            }
        End Function

        Private Function HistoricoEstado() As JObject
            Dim e = Descarga.ObtenerEstado()
            Return New JObject From {{"ok", True}, {"estado", EstadoDescargaJson(e)}}
        End Function

        ''' <summary>
        ''' body: {estrategia, symbol, timeframe, duracion, confianza, payout, corte:"YYYY-MM-DD", [desde:"YYYY-MM-DD"]}
        ''' </summary>
        Private Async Function ValidarEstrategiaApi(body As JObject) As Task(Of JObject)
            If body Is Nothing Then Return ErrJson("Body: {estrategia, symbol, timeframe, duracion, confianza, payout, corte}")
            Dim nombre = If(body("estrategia")?.ToString(), "")
            EstrategiasDinamicasRegistry.Inicializar()
            Dim est = EstrategiasDinamicasRegistry.GetByNombre(nombre)
            If est Is Nothing Then Return ErrJson("Estrategia no encontrada: " & nombre)
            Dim corte As DateTime
            If Not DateTime.TryParse(If(body("corte")?.ToString(), ""), Globalization.CultureInfo.InvariantCulture,
                                     Globalization.DateTimeStyles.AssumeUniversal Or Globalization.DateTimeStyles.AdjustToUniversal, corte) Then
                Return ErrJson("corte requerido (YYYY-MM-DD)")
            End If
            Dim desdeEpoch As Long = 0
            Dim desde As DateTime
            If DateTime.TryParse(If(body("desde")?.ToString(), ""), Globalization.CultureInfo.InvariantCulture,
                                 Globalization.DateTimeStyles.AssumeUniversal Or Globalization.DateTimeStyles.AdjustToUniversal, desde) Then
                desdeEpoch = New DateTimeOffset(desde).ToUnixTimeSeconds()
            End If
            Try
                Return Await ValidacionEstrategia.ValidarAsync(
                    est,
                    If(body("symbol")?.ToString(), ""), If(body("timeframe")?.ToString(), ""),
                    If(body("duracion") IsNot Nothing, CInt(body("duracion")), 2),
                    If(body("confianza") IsNot Nothing, CInt(body("confianza")), 55),
                    If(body("payout") IsNot Nothing, CDec(body("payout")), 85D),
                    New DateTimeOffset(corte).ToUnixTimeSeconds(), desdeEpoch)
            Catch ex As Exception
                Return ErrJson(ex.Message)
            End Try
        End Function

        Private Function EstadoDescargaJson(e As DescargaJobEstado) As JObject
            Return New JObject From {
                {"symbol", e.Symbol}, {"timeframe", e.Timeframe},
                {"desde", e.Desde}, {"hasta", e.Hasta},
                {"activo", e.EstaActivo}, {"completado", e.Completado},
                {"error", e.TuvoError}, {"velas_guardadas", e.VelasGuardadas},
                {"pagina", e.Pagina}, {"mensaje", e.UltimoMensaje}
            }
        End Function

        Private Function AbrirVentana(body As JObject) As JObject
            Dim nombre = If(body?("nombre")?.ToString(), "").ToLowerInvariant()
            Try
                Select Case nombre
                    Case "trading", "mainwindow", "pruebas"
                        UiDispatcher.Invoke(Sub() AgentHost.AbrirVentanaTrading(minimizada:=True))
                    Case "servidorlive", "servidor"
                        UiDispatcher.Invoke(Sub() AgentHost.AbrirVentanaServidorLive())
                    Case "heavy", "backtest"
                        UiDispatcher.Invoke(Sub() AgentHost.AbrirVentanaHeavy())
                    Case Else
                        Return ErrJson("nombre debe ser: trading | servidorlive | heavy")
                End Select
                Return New JObject From {{"ok", True}, {"ventana", nombre}}
            Catch ex As Exception
                Return ErrJson(ex.Message)
            End Try
        End Function

        ' ===================================================
        '  HELPERS
        ' ===================================================

        Private Function SesionToJson(s As BacktestSesion) As JObject
            Return New JObject From {
                {"id", s.Id},
                {"estrategia", s.Estrategia},
                {"symbol", s.Symbol},
                {"timeframe", s.Timeframe},
                {"desde_epoch", s.DesdeEpoch},
                {"hasta_epoch", s.HastaEpoch},
                {"duracion_velas", s.DuracionVelas},
                {"payout_pct", s.PayoutPct},
                {"min_confianza", s.MinConfianza},
                {"total_senales", s.TotalSenales},
                {"total_operaciones", s.TotalOperaciones},
                {"win_rate", s.WinRate},
                {"profit_loss_total", s.ProfitLossTotal},
                {"max_drawdown", s.MaxDrawdown},
                {"profit_factor", s.ProfitFactor},
                {"created_at", s.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")},
                {"notas", If(s.Notas, "")}
            }
        End Function

        Private Sub ParseDesdeHasta(body As JObject, sel As HeavySelection)
            If body("desde_epoch") IsNot Nothing Then
                sel.DesdeEpoch = CLng(body("desde_epoch"))
            ElseIf body("desde_iso") IsNot Nothing Then
                Dim dt As DateTime
                If DateTime.TryParse(body("desde_iso").ToString(), dt) Then
                    sel.DesdeEpoch = New DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeSeconds()
                End If
            End If
            If body("hasta_epoch") IsNot Nothing Then
                sel.HastaEpoch = CLng(body("hasta_epoch"))
            ElseIf body("hasta_iso") IsNot Nothing Then
                Dim dt As DateTime
                If DateTime.TryParse(body("hasta_iso").ToString(), dt) Then
                    sel.HastaEpoch = New DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeSeconds()
                End If
            End If
        End Sub

        Private Function JsonStringList(body As JObject, key As String) As List(Of String)
            Dim r As New List(Of String)()
            Dim arr = TryCast(body(key), JArray)
            If arr Is Nothing Then Return r
            For Each it In arr
                Dim s = it.ToString()
                If Not String.IsNullOrWhiteSpace(s) Then r.Add(s)
            Next
            Return r
        End Function

        Private Function JsonIntList(body As JObject, key As String) As List(Of Integer)
            Dim r As New List(Of Integer)()
            Dim arr = TryCast(body(key), JArray)
            If arr Is Nothing Then Return r
            For Each it In arr
                Dim n As Integer
                If Integer.TryParse(it.ToString(), n) Then r.Add(n)
            Next
            Return r
        End Function

        Private Function JsonDecimalList(body As JObject, key As String) As List(Of Decimal)
            Dim r As New List(Of Decimal)()
            Dim arr = TryCast(body(key), JArray)
            If arr Is Nothing Then Return r
            For Each it In arr
                Dim n As Decimal
                If Decimal.TryParse(it.ToString().Replace(",", "."),
                                     Globalization.NumberStyles.Any,
                                     Globalization.CultureInfo.InvariantCulture, n) Then
                    r.Add(n)
                End If
            Next
            Return r
        End Function

        Private Async Function WriteJson(res As HttpListenerResponse, code As Integer, j As JObject) As Task
            Dim body = j.ToString(Formatting.Indented)
            res.StatusCode = code
            Dim bytes = Encoding.UTF8.GetBytes(body)
            res.ContentLength64 = bytes.Length
            Await res.OutputStream.WriteAsync(bytes, 0, bytes.Length)
            res.OutputStream.Close()
        End Function

        Private Function ErrJson(msg As String) As JObject
            Return New JObject From {{"ok", False}, {"error", msg}}
        End Function

    End Class

End Namespace

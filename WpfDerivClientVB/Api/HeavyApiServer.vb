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
            Tareas.EnSegundoPlano(Task.Run(Function() ListenLoop()), "HeavyApi: bucle de escucha")
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
                    Tareas.EnSegundoPlano(Task.Run(Function() HandleRequest(ctx)), "HeavyApi: peticion")
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
                ' Activa/quita la reanudacion automatica de una sesion: {"sesion_id":N, "activar":true}
                If path = "/api/heavy/live/autonomo" AndAlso method = "POST" Then
                    Dim sidA = If(body?("sesion_id") IsNot Nothing, CInt(body("sesion_id")), 0)
                    Dim activar = body?("activar") Is Nothing OrElse CBool(body("activar"))
                    Dim existe = sidA > 0 AndAlso Await New LiveTestRepository().MarcarReanudacionAutomaticaAsync(sidA, activar)
                    Await WriteJson(res, 200, New JObject From {{"ok", existe}, {"sesion_id", sidA}, {"reanudacion_automatica", activar}})
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

                ' Compra automatica SOLO en cuenta demo (forward tests). Pasa por el gestor de riesgo.
                If path = "/api/heavy/trading/comprar-demo" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await ComprarDemo(body))
                    Return
                End If

                ' Consulta de solo lectura por la conexion autenticada (lista blanca en el servicio)
                If path = "/api/heavy/trading/consulta" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await DerivTradingService.Instance.ConsultarAsync(body))
                    Return
                End If

                ' Salud del modo autonomo: conexion, sesiones, velas por sesion, alertas y mantenimiento
                If path = "/api/heavy/salud" Then
                    Await WriteJson(res, 200, New JObject From {{"ok", True}, {"salud", ModoAutonomo.Estado()}})
                    Return
                End If
                ' Mantenimiento manual: {"velas":true, "respaldo":true}. Corre en segundo plano.
                If path = "/api/heavy/mantenimiento/ejecutar" AndAlso method = "POST" Then
                    Dim velas = If(body?("velas") IsNot Nothing, CBool(body("velas")), True)
                    Dim respaldo = If(body?("respaldo") IsNot Nothing, CBool(body("respaldo")), True)
                    Dim tarea = Task.Run(Function() ModoAutonomo.EjecutarMantenimientoAsync(velas, respaldo))
                    Await WriteJson(res, 200, New JObject From {{"ok", True}, {"mensaje", "Mantenimiento iniciado. Ver GET /api/heavy/salud."}})
                    Return
                End If

                ' Multiplicadores: backtest con eleccion de configuracion en diseno y prueba en ciego
                If path = "/api/heavy/multiplicadores/validar" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await MultiplicadoresValidarApi(body))
                    Return
                End If

                ' Multiplicadores: cotiza (sin comprar) para medir comision y stop out
                If path = "/api/heavy/multiplicadores/cotizar" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await MultiplicadoresCotizarApi(body))
                    Return
                End If

                ' Diagnostico: suscribe velas en vivo unos segundos y cuenta los ticks recibidos
                If path = "/api/heavy/trading/probar-velas" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await ProbarVelasApi(body))
                    Return
                End If

                ' Validacion fuera de muestra de una estrategia (no guarda nada)
                If path = "/api/heavy/validar" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await ValidarEstrategiaApi(body))
                    Return
                End If

                ' Embudo de hipotesis: registrar con motivo, validar y corregir por pruebas multiples
                If path = "/api/heavy/hipotesis" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await HipotesisApi(body))
                    Return
                End If
                If path = "/api/heavy/hipotesis" AndAlso method = "GET" Then
                    Await WriteJson(res, 200, New JObject From {{"ok", True}, {"hipotesis", Await New HipotesisRepository().ListarAsync(200)}})
                    Return
                End If

                ' Validacion completa con informe (lo mismo que el boton Validar del Heavy Tester)
                If path = "/api/heavy/validar-completo" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await ValidarCompletoApi(body))
                    Return
                End If

                ' Walk-forward + Monte Carlo + ruido en precios (no guarda nada)
                If path = "/api/heavy/robustez" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await RobustezApi(body))
                    Return
                End If

                ' Payout real: cotiza CALL y PUT por activo y duracion (proposal, NUNCA compra)
                If path = "/api/heavy/payouts" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await PayoutsApi(body))
                    Return
                End If

                ' La misma regla en varios mercados/temporalidades (diseno vs ciego)
                If path = "/api/heavy/multimercado" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, Await MultiMercadoApi(body))
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

                ' -- Historico largo externo (Dukascopy, Bitstamp, Binance, Yahoo -> Postgres, platform = fuente) --
                If path = "/api/heavy/historico-externo/descargar" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, HistoricoExternoDescargar(body))
                    Return
                End If
                If path = "/api/heavy/historico-externo/estado" Then
                    Await WriteJson(res, 200, HistoricoExternoEstado(qs("fuente")))
                    Return
                End If
                If path = "/api/heavy/historico-externo/detener" AndAlso method = "POST" Then
                    Await WriteJson(res, 200, HistoricoExternoDetener(If(qs("fuente"), body?("fuente")?.ToString())))
                    Return
                End If
                If path = "/api/heavy/historico-externo/cobertura" Then
                    Await WriteJson(res, 200, Await HistoricoCoberturaApi())
                    Return
                End If
                If path = "/api/heavy/historico-externo/comparar" Then
                    Dim fuenteCmp As String
                    Try
                        fuenteCmp = FuentesVelas.Normalizar(If(qs("fuente"), FuentesVelas.DUKASCOPY))
                    Catch ex As ArgumentException
                        fuenteCmp = Nothing
                    End Try
                    If fuenteCmp Is Nothing Then
                        Await WriteJson(res, 200, ErrJson("Fuente desconocida: " & qs("fuente")))
                        Return
                    End If
                    Await WriteJson(res, 200, Await DukascopyDescarga.CompararAsync(
                        If(qs("symbol"), "frxEURUSD"), If(qs("timeframe"), "1h"), fuenteCmp))
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
                "POST /api/heavy/trading/comprar-demo (body: {contract_type:CALL|PUT, symbol, amount, duration, duration_unit})  solo cuenta demo, pasa por el gestor de riesgo",
                "POST /api/heavy/app/ventana      (body: {nombre: trading|servidorlive|heavy})",
                "GET  /api/heavy/riesgo/estado",
                "POST /api/heavy/riesgo/parar     (body: {motivo})",
                "POST /api/heavy/riesgo/reanudar",
                "POST /api/heavy/historico/descargar (body: {symbol, timeframe, desde:YYYY-MM-DD, hasta:YYYY-MM-DD})",
                "GET  /api/heavy/historico/estado",
                "POST /api/heavy/historico/detener",
                "POST /api/heavy/historico-externo/descargar (body: {[fuente: Dukascopy | Bitstamp | Binance | Yahoo], simbolos:[frxEURUSD,...], temporalidades:[1h,4h,1d], [desde:YYYY-MM-DD], [hasta]})",
                "GET  /api/heavy/historico-externo/estado[?fuente=] | POST .../detener[?fuente=] | GET .../cobertura",
                "GET  /api/heavy/historico-externo/comparar?symbol=frxEURUSD&timeframe=1h[&fuente=Dukascopy] (contra Deriv)",
                "(validar, robustez, multimercado, validar-completo, hipotesis, jobs/agregar aceptan fuente: Deriv | Dukascopy | Bitstamp | Binance | Yahoo)",
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
                ' "payout_real": true -> cada job usa el payout medido en Deriv si existe
                sel.UsarPayoutReal = body("payout_real") IsNot Nothing AndAlso CBool(body("payout_real"))
                ' "fuente": "Dukascopy" -> los jobs corren sobre el historico largo externo
                sel.Fuente = FuentesVelas.Normalizar(body("fuente")?.ToString())
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
                    .Fuente = FuentesVelas.Normalizar(body("fuente")?.ToString()),
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
            Dim operacionUi = UiDispatcher.BeginInvoke(Sub()
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
                ' "autonomo": true -> el modo autonomo la reanudara tras un reinicio
                Dim autonomo = body("autonomo") IsNot Nothing AndAlso CBool(body("autonomo"))
                If autonomo Then Await New LiveTestRepository().MarcarReanudacionAutomaticaAsync(sid, True)
                Return New JObject From {{"ok", True}, {"sesion_id", sid}, {"reanudacion_automatica", autonomo}}
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

        ''' <summary>
        ''' Compra automatica para forward tests, SOLO si la cuenta conectada es demo. Usa la misma
        ''' ruta que las estrategias (ExecuteTradeAsync), asi que pasa por el gestor de riesgo.
        ''' body: {contract_type: CALL|PUT, symbol, amount, duration, duration_unit, [basis, _meta_estrategia]}
        ''' </summary>
        Private Async Function ComprarDemo(body As JObject) As Task(Of JObject)
            If body Is Nothing Then Return ErrJson("Body: {contract_type, symbol, amount, duration, duration_unit}")
            Dim svc = DerivTradingService.Instance
            If Not svc.EstaConectado Then Return ErrJson("Trading no conectado: POST /api/heavy/trading/conectar {""demo"":true}")
            If Not svc.EsDemo Then Return ErrJson("comprar-demo solo opera en la cuenta DEMO y la conectada es REAL.")
            Dim tipo = If(body("contract_type")?.ToString(), "").ToUpperInvariant()
            If tipo <> "CALL" AndAlso tipo <> "PUT" Then Return ErrJson("contract_type debe ser CALL o PUT.")
            Return Await svc.ExecuteTradeAsync(body)
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

        ''' <summary>body: {simbolos:[...] o symbol, temporalidades:[...] (por defecto 1h,4h,1d), desde:"YYYY-MM-DD" (por defecto 2003-05-05), hasta}</summary>
        Private Function HistoricoExternoDescargar(body As JObject) As JObject
            If body Is Nothing Then body = New JObject()
            Dim simbolos As New List(Of String)()
            If TypeOf body("simbolos") Is JArray Then
                simbolos.AddRange(CType(body("simbolos"), JArray).Select(Function(t) t.ToString()))
            ElseIf body("symbol") IsNot Nothing Then
                simbolos.Add(body("symbol").ToString())
            End If
            Dim tfs As New List(Of String)()
            If TypeOf body("temporalidades") Is JArray Then tfs.AddRange(CType(body("temporalidades"), JArray).Select(Function(t) t.ToString()))
            Dim inv = Globalization.CultureInfo.InvariantCulture
            Dim desde, hasta As DateTime
            Dim descarga As IDescargaHistorica
            Try
                descarga = DescargasHistoricas.Para(If(body("fuente")?.ToString(), FuentesVelas.DUKASCOPY))
            Catch ex As ArgumentException
                Return ErrJson(ex.Message)
            End Try
            If descarga Is Nothing Then Return ErrJson("Deriv no es una fuente externa: sus velas se bajan con /api/historico/descargar (8765) o descargar_historico.")
            If Not DateTime.TryParseExact(If(body("desde")?.ToString(), descarga.DesdePorDefecto.ToString("yyyy-MM-dd")), "yyyy-MM-dd", inv, Globalization.DateTimeStyles.None, desde) Then
                Return ErrJson("'desde' no valido (YYYY-MM-DD)")
            End If
            If Not DateTime.TryParseExact(If(body("hasta")?.ToString(), DateTime.UtcNow.ToString("yyyy-MM-dd")), "yyyy-MM-dd", inv, Globalization.DateTimeStyles.None, hasta) Then
                Return ErrJson("'hasta' no valido (YYYY-MM-DD)")
            End If
            Return descarga.Iniciar(simbolos, tfs, desde, hasta)
        End Function

        ''' <summary>Sin fuente: {estado: Dukascopy (compatibilidad), estados: {fuente: estado}}. Con fuente: {estado}.</summary>
        Private Function HistoricoExternoEstado(fuente As String) As JObject
            If Not String.IsNullOrWhiteSpace(fuente) Then
                Dim d As IDescargaHistorica
                Try
                    d = DescargasHistoricas.Para(fuente)
                Catch ex As ArgumentException
                    Return ErrJson(ex.Message)
                End Try
                If d Is Nothing Then Return ErrJson("Deriv no es una fuente externa (su descarga es /api/historico/estado en 8765).")
                Return New JObject From {{"ok", True}, {"fuente", d.Fuente}, {"estado", d.Estado()}}
            End If
            Dim estados As New JObject()
            For Each d In DescargasHistoricas.Todas()
                estados(d.Fuente) = d.Estado()
            Next
            Return New JObject From {{"ok", True}, {"estado", DukascopyDescarga.Instancia.Estado()}, {"estados", estados}}
        End Function

        ''' <summary>Detiene la descarga de una fuente o, sin fuente, todas.</summary>
        Private Function HistoricoExternoDetener(fuente As String) As JObject
            Dim lista As List(Of IDescargaHistorica)
            If String.IsNullOrWhiteSpace(fuente) Then
                lista = DescargasHistoricas.Todas()
            Else
                Dim d As IDescargaHistorica
                Try
                    d = DescargasHistoricas.Para(fuente)
                Catch ex As ArgumentException
                    Return ErrJson(ex.Message)
                End Try
                If d Is Nothing Then Return ErrJson("Deriv no es una fuente externa.")
                lista = New List(Of IDescargaHistorica) From {d}
            End If
            For Each d In lista
                d.Detener()
            Next
            Return New JObject From {{"ok", True}, {"mensaje", "Detencion solicitada: " & String.Join(", ", lista.Select(Function(x) x.Fuente))}}
        End Function

        ''' <summary>Cobertura de cada fuente por serie (velas, desde, hasta).</summary>
        Private Async Function HistoricoCoberturaApi() As Task(Of JObject)
            Dim repo As New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString())
            Dim fecha = Function(ep As Long) DateTimeOffset.FromUnixTimeSeconds(ep).UtcDateTime.ToString("yyyy-MM-dd")
            Dim res As New JObject From {{"ok", True}}
            For Each f In FuentesVelas.Todas
                Dim arr As New JArray()
                For Each c In Await repo.CoberturaAsync(f)
                    arr.Add(New JObject From {{"symbol", c.Item1}, {"timeframe", c.Item2}, {"velas", c.Item3}, {"desde", fecha(c.Item4)}, {"hasta", fecha(c.Item5)}})
                Next
                res(f.ToLowerInvariant()) = arr
            Next
            Return res
        End Function

        Private Function HistoricoEstado() As JObject
            Dim e = Descarga.ObtenerEstado()
            Return New JObject From {{"ok", True}, {"estado", EstadoDescargaJson(e)}}
        End Function

        ''' <summary>
        ''' body: {estrategia, symbol, timeframe, duracion, confianza, payout, corte:"YYYY-MM-DD", [desde:"YYYY-MM-DD"],
        '''        [fuente], [incluir_operaciones:true -> lista [entrada, salida, dir, ganada] de cada operacion]}
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
                Dim symV = If(body("symbol")?.ToString(), ""), tfV = If(body("timeframe")?.ToString(), "")
                Dim durV = If(body("duracion") IsNot Nothing, CInt(body("duracion")), 2)
                Return Await ValidacionEstrategia.ValidarAsync(
                    est, symV, tfV, durV,
                    If(body("confianza") IsNot Nothing, CInt(body("confianza")), 55),
                    PayoutsReales.Resolver(body("payout"), symV, tfV, durV),
                    New DateTimeOffset(corte).ToUnixTimeSeconds(), desdeEpoch,
                    fuente:=FuentesVelas.Normalizar(body("fuente")?.ToString()),
                    incluirOperaciones:=body("incluir_operaciones") IsNot Nothing AndAlso CBool(body("incluir_operaciones")))
            Catch ex As Exception
                Return ErrJson(ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' body: {estrategia, symbol, timeframe, duracion, confianza, payout,
        '''        [dias_entrenamiento=365, dias_prueba=90, grilla=[50,55,60,65,70],
        '''         iteraciones=5000, corridas_ruido=30, nivel_ruido=0.1]}
        ''' </summary>
        Private Async Function RobustezApi(body As JObject) As Task(Of JObject)
            If body Is Nothing Then Return ErrJson("Body: {estrategia, symbol, timeframe, duracion, confianza, payout}")
            EstrategiasDinamicasRegistry.Inicializar()
            Dim est = EstrategiasDinamicasRegistry.GetByNombre(If(body("estrategia")?.ToString(), ""))
            If est Is Nothing Then Return ErrJson("Estrategia no encontrada.")
            Dim symbol = If(body("symbol")?.ToString(), ""), tf = If(body("timeframe")?.ToString(), "")
            Dim cfg As New BacktestConfig With {
                .Symbol = symbol, .Timeframe = tf,
                .DuracionVelas = If(body("duracion") IsNot Nothing, CInt(body("duracion")), 2),
                .MinConfianza = If(body("confianza") IsNot Nothing, CInt(body("confianza")), 55),
                .PayoutPct = PayoutsReales.Resolver(body("payout"), symbol, tf, If(body("duracion") IsNot Nothing, CInt(body("duracion")), 2)),
                .VentanaIndicadores = 100, .UnaPosicionALaVez = True
            }
            cfg.Cuentas.Add(New BacktestCuenta With {.NombreCuenta = "robustez", .BalanceInicial = 1000D, .MontoPorOperacion = 1D})
            Dim grilla As New List(Of Integer)()
            If TypeOf body("grilla") Is JArray Then
                grilla.AddRange(CType(body("grilla"), JArray).Select(Function(t) CInt(t)))
            Else
                grilla.AddRange({50, 55, 60, 65, 70})
            End If
            ' Corte opcional: separa las ventanas de prueba anteriores y posteriores al diseno
            Dim corteRobustez As Long = 0
            Dim corteR As DateTime
            If DateTime.TryParse(If(body("corte")?.ToString(), ""), Globalization.CultureInfo.InvariantCulture,
                                 Globalization.DateTimeStyles.AssumeUniversal Or Globalization.DateTimeStyles.AdjustToUniversal, corteR) Then
                corteRobustez = New DateTimeOffset(corteR).ToUnixTimeSeconds()
            End If
            Try
                Return Await ValidacionCompleta.RobustezAsync(est, symbol, tf, cfg.DuracionVelas, cfg.MinConfianza, cfg.PayoutPct,
                    corteRobustez,
                    If(body("dias_entrenamiento") IsNot Nothing, CInt(body("dias_entrenamiento")), 365),
                    If(body("dias_prueba") IsNot Nothing, CInt(body("dias_prueba")), 90),
                    grilla,
                    If(body("iteraciones") IsNot Nothing, CInt(body("iteraciones")), 5000),
                    If(body("corridas_ruido") IsNot Nothing, CInt(body("corridas_ruido")), 30),
                    If(body("nivel_ruido") IsNot Nothing, CDbl(body("nivel_ruido")), 0.1),
                    FuentesVelas.Normalizar(body("fuente")?.ToString()))
            Catch ex As Exception
                Return ErrJson(ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' body: {estrategia, duracion, confianza, payout, corte:"YYYY-MM-DD",
        '''        mercados:[["OTC_NDX","1h"], ["frxEURUSD","4h"], ...]}
        ''' </summary>
        Private Async Function MultiMercadoApi(body As JObject) As Task(Of JObject)
            If body Is Nothing OrElse Not TypeOf body("mercados") Is JArray Then Return ErrJson("Body: {estrategia, duracion, confianza, payout, corte, mercados:[[symbol,tf],...]}")
            EstrategiasDinamicasRegistry.Inicializar()
            Dim est = EstrategiasDinamicasRegistry.GetByNombre(If(body("estrategia")?.ToString(), ""))
            If est Is Nothing Then Return ErrJson("Estrategia no encontrada.")
            Dim corte As DateTime
            If Not DateTime.TryParse(If(body("corte")?.ToString(), ""), Globalization.CultureInfo.InvariantCulture,
                                     Globalization.DateTimeStyles.AssumeUniversal Or Globalization.DateTimeStyles.AdjustToUniversal, corte) Then
                Return ErrJson("corte requerido (YYYY-MM-DD)")
            End If
            Dim corteEpoch = New DateTimeOffset(corte).ToUnixTimeSeconds()
            Dim dur = If(body("duracion") IsNot Nothing, CInt(body("duracion")), 2)
            Dim conf = If(body("confianza") IsNot Nothing, CInt(body("confianza")), 55)
            Dim lista = CType(body("mercados"), JArray).Select(Function(m) Tuple.Create(m(0).ToString(), m(1).ToString())).ToList()
            ' Sin payout (o "real"): cada mercado con su payout medido en Deriv, o 85 si no se ha medido
            Dim pedido = body("payout")
            Dim fuenteM As String
            Try
                fuenteM = FuentesVelas.Normalizar(body("fuente")?.ToString())
            Catch ex As ArgumentException
                Return ErrJson(ex.Message)
            End Try
            If pedido Is Nothing OrElse String.Equals(pedido.ToString(), "real", StringComparison.OrdinalIgnoreCase) Then
                Return Await ValidacionCompleta.MultiMercadoAsync(est, lista, dur, conf, 85D, corteEpoch,
                    Function(sym, tfM) PayoutsReales.Resolver(Nothing, sym, tfM, dur), fuenteM)
            End If
            Return Await ValidacionCompleta.MultiMercadoAsync(est, lista, dur, conf, CDec(pedido), corteEpoch, fuente:=fuenteM)
        End Function

        ''' <summary>
        ''' Mide el payout real que ofrece Deriv ahora mismo. Por cada mercado pide una
        ''' cotizacion (proposal) CALL y otra PUT con la duracion que usa el backtest
        ''' (velas x timeframe). No compra nada: las cotizaciones caducan solas.
        ''' body: {mercados:[[symbol, timeframe, velas], ...], monto=10}
        ''' </summary>
        Private Async Function PayoutsApi(body As JObject) As Task(Of JObject)
            If body Is Nothing OrElse Not TypeOf body("mercados") Is JArray Then
                Return ErrJson("Body: {mercados:[[symbol, timeframe, velas], ...], monto}")
            End If
            Dim svc = DerivTradingService.Instance
            If Not svc.EstaConectado Then Return ErrJson("Trading no conectado: POST /api/heavy/trading/conectar {""demo"":true}")
            Dim monto = If(body("monto") IsNot Nothing, CDbl(body("monto")), 10.0)
            Dim filas As New JArray()
            For Each m In CType(body("mercados"), JArray)
                Dim mercado = CType(m, JArray)
                Dim sym = mercado(0).ToString(), tf = mercado(1).ToString()
                Dim velas = If(mercado.Count > 2, CInt(mercado(2)), 2)
                Dim unidad As String = ""
                Dim dur = LiveTestMotor.DuracionDeriv(tf, velas, unidad)
                Dim fila As New JObject From {{"symbol", sym}, {"timeframe", tf}, {"velas", velas}, {"duracion", dur.ToString() & unidad}}
                If dur <= 0 Then
                    fila("error") = "Duracion no soportada por Deriv"
                    filas.Add(fila)
                    Continue For
                End If
                Dim pagos As New List(Of Double)()
                For Each tipo In {"CALL", "PUT"}
                    Dim c = Await svc.CotizarAsync(tipo, sym, monto, dur, unidad)
                    ' Deriv limita las cotizaciones por minuto: esperar y reintentar una vez
                    If If(c?("error")?.ToString(), "").Contains("RateLimit") Then
                        Await Task.Delay(35000)
                        c = Await svc.CotizarAsync(tipo, sym, monto, dur, unidad)
                    End If
                    If c IsNot Nothing AndAlso c("ok") IsNot Nothing AndAlso CBool(c("ok")) AndAlso CDbl(c("ask_price")) > 0 Then
                        Dim ask = CDbl(c("ask_price")), pago = CDbl(c("payout"))
                        Dim pct = Math.Round((pago - ask) / ask * 100.0, 2)
                        fila(tipo.ToLowerInvariant() & "_payout_pct") = pct
                        pagos.Add(pct)
                    Else
                        fila(tipo.ToLowerInvariant() & "_error") = If(c?("error")?.ToString(), "sin respuesta")
                    End If
                    Await Task.Delay(1200)
                Next
                If pagos.Count > 0 Then
                    Dim medio = pagos.Average()
                    fila("payout_pct") = Math.Round(medio, 2)
                    fila("punto_equilibrio") = Math.Round(Estadistica.PuntoEquilibrio(CDec(medio)), 4)
                End If
                filas.Add(fila)
            Next
            Dim medido = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            ' El Heavy Strategy Tester usa estas mediciones en vez de un payout supuesto
            PayoutsReales.Registrar(filas, medido)
            Return New JObject From {
                {"ok", True}, {"medido_utc", medido},
                {"monto", monto}, {"cuenta", If(svc.EsDemo, "demo", "real")}, {"mercados", filas}
            }
        End Function

        ''' <summary>
        ''' Diagnostico del streaming: suscribe (symbol, granularidad) durante unos segundos por la
        ''' conexion autenticada, cuenta los ticks "ohlc" y cancela la suscripcion.
        ''' body: {symbol, granularidad=60, segundos=10}
        ''' </summary>
        Private Async Function ProbarVelasApi(body As JObject) As Task(Of JObject)
            Dim symbol = If(body?("symbol")?.ToString(), "R_100")
            Dim gran = If(body?("granularidad") IsNot Nothing, CInt(body("granularidad")), 60)
            Dim segundos = Math.Min(60, Math.Max(1, If(body?("segundos") IsNot Nothing, CInt(body("segundos")), 10)))
            Dim svc = DerivTradingService.Instance
            Dim ticks As Integer = 0
            Dim ultimo As JObject = Nothing
            Dim r = Await svc.SuscribirVelasAsync(symbol, gran, 5,
                Sub(ohlc)
                    Threading.Interlocked.Increment(ticks)
                    ultimo = ohlc
                End Sub)
            If r Is Nothing OrElse r("ok") Is Nothing OrElse Not CBool(r("ok")) Then
                Return ErrJson(If(r?("error")?.ToString(), "sin respuesta"))
            End If
            Dim subId = r("subscription")?("id")?.ToString()
            Await Task.Delay(segundos * 1000)
            Await svc.CancelarSuscripcionVelasAsync(subId)
            Dim velas = TryCast(r("candles"), JArray)
            Return New JObject From {
                {"ok", True}, {"symbol", symbol}, {"granularidad", gran}, {"segundos", segundos},
                {"subscription_id", If(subId, "")}, {"velas_iniciales", If(velas Is Nothing, 0, velas.Count)},
                {"ticks_recibidos", ticks}, {"ultimo_ohlc", If(ultimo, CType(JValue.CreateNull(), JToken))}
            }
        End Function

        ''' <summary>
        ''' Mide la comision real de los multiplicadores: por cada [symbol, multiplicador] cotiza
        ''' MULTUP y MULTDOWN (proposal, NUNCA compra). body: {mercados:[[symbol, mult], ...], monto=10}
        ''' comision_pct = comision / (monto x multiplicador): fraccion del nocional que se paga al entrar.
        ''' </summary>
        Private Async Function MultiplicadoresCotizarApi(body As JObject) As Task(Of JObject)
            If body Is Nothing OrElse Not TypeOf body("mercados") Is JArray Then
                Return ErrJson("Body: {mercados:[[symbol, multiplicador], ...], monto}")
            End If
            Dim svc = DerivTradingService.Instance
            If Not svc.EstaConectado Then Return ErrJson("Trading no conectado: POST /api/heavy/trading/conectar {""demo"":true}")
            Dim monto = If(body("monto") IsNot Nothing, CDbl(body("monto")), 10.0)
            Dim filas As New JArray()
            For Each m In CType(body("mercados"), JArray)
                Dim sym = m(0).ToString()
                Dim mult = CInt(m(1))
                Dim fila As New JObject From {{"symbol", sym}, {"multiplicador", mult}}
                For Each tipo In {"MULTUP", "MULTDOWN"}
                    Dim c = Await svc.CotizarMultiplicadorAsync(tipo, sym, monto, mult)
                    If If(c?("error")?.ToString(), "").Contains("RateLimit") Then
                        Await Task.Delay(35000)
                        c = Await svc.CotizarMultiplicadorAsync(tipo, sym, monto, mult)
                    End If
                    Dim clave = tipo.ToLowerInvariant()
                    If c IsNot Nothing AndAlso c("ok") IsNot Nothing AndAlso CBool(c("ok")) Then
                        Dim prop = c("proposal")
                        Dim comision = If(prop?("commission") IsNot Nothing, CDbl(prop("commission")), 0.0)
                        fila(clave & "_comision") = comision
                        fila(clave & "_comision_pct") = Math.Round(comision / (monto * mult), 7)
                        fila(clave & "_stop_out") = prop?("limit_order")?("stop_out")?.DeepClone()
                        fila(clave & "_spot") = prop?("spot")?.DeepClone()
                    Else
                        fila(clave & "_error") = If(c?("error")?.ToString(), "sin respuesta")
                    End If
                    Await Task.Delay(1200)
                Next
                filas.Add(fila)
            Next
            Return New JObject From {
                {"ok", True}, {"medido_utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")},
                {"monto", monto}, {"mercados", filas}
            }
        End Function

        ''' <summary>
        ''' Valida una estrategia operada con multiplicadores. En cada mercado se prueban todas las
        ''' combinaciones (multiplicador x [take profit, stop loss] x velas maximas), se ELIGE la mejor
        ''' solo con el periodo de diseno (minimo min_ops operaciones) y se mide esa misma en el
        ''' periodo ciego. TP y SL van en multiplos del stake (stake = 1: resultados por dolar).
        ''' body: {estrategia, confianza, corte, [ventana=100, min_ops=30],
        '''        mercados:[[symbol, tf, comision_pct, [multiplicadores], [[tp, sl], ...], [max_velas]], ...],
        '''        multiplicadores:[...], tp_sl:[[tp, sl], ...], max_velas:[...],
        '''        [fuente: Deriv | Dukascopy | Bitstamp | Binance | Yahoo], [desde, hasta: "YYYY-MM-DD" -> solo velas en [desde, hasta)]}
        ''' Los tres ultimos campos de cada mercado son opcionales y reemplazan a los globales: con un
        ''' solo valor en cada uno la configuracion queda FIJA (sirve para medir en otro periodo una
        ''' configuracion ya elegida).
        ''' </summary>
        Private Shared ReadOnly _cacheSenalesMult As New System.Collections.Concurrent.ConcurrentDictionary(Of String, List(Of SenalMultiplicador))()

        Private Async Function MultiplicadoresValidarApi(body As JObject) As Task(Of JObject)
            If body Is Nothing OrElse Not TypeOf body("mercados") Is JArray Then
                Return ErrJson("Body: {estrategia, confianza, corte, mercados:[[symbol, tf, comision_pct], ...], multiplicadores, tp_sl, max_velas}")
            End If
            EstrategiasDinamicasRegistry.Inicializar()
            Dim est = EstrategiasDinamicasRegistry.GetByNombre(If(body("estrategia")?.ToString(), ""))
            If est Is Nothing Then Return ErrJson("Estrategia no encontrada.")
            Dim corte As DateTime
            If Not DateTime.TryParse(If(body("corte")?.ToString(), ""), Globalization.CultureInfo.InvariantCulture,
                                     Globalization.DateTimeStyles.AssumeUniversal Or Globalization.DateTimeStyles.AdjustToUniversal, corte) Then
                Return ErrJson("corte requerido (YYYY-MM-DD)")
            End If
            Dim corteEpoch = New DateTimeOffset(corte).ToUnixTimeSeconds()
            Dim conf = If(body("confianza") IsNot Nothing, CInt(body("confianza")), 55)
            Dim ventana = If(body("ventana") IsNot Nothing, CInt(body("ventana")), 100)
            Dim minOps = If(body("min_ops") IsNot Nothing, CInt(body("min_ops")), 30)
            Dim multsGlobal = If(TypeOf body("multiplicadores") Is JArray,
                                 CType(body("multiplicadores"), JArray).Select(Function(t) CInt(t)).ToList(), New List(Of Integer) From {100})
            Dim tpSlGlobal As New List(Of Decimal())()
            If TypeOf body("tp_sl") Is JArray Then
                For Each par In CType(body("tp_sl"), JArray)
                    tpSlGlobal.Add({CDec(par(0)), CDec(par(1))})
                Next
            Else
                tpSlGlobal.Add({0D, 0D})
            End If
            Dim maxVelasGlobal = If(TypeOf body("max_velas") Is JArray,
                                    CType(body("max_velas"), JArray).Select(Function(t) CInt(t)).ToList(), New List(Of Integer) From {6})
            Dim fuente = FuentesVelas.Normalizar(body("fuente")?.ToString())
            Dim desdeEpoch As Long = Long.MinValue, hastaEpoch As Long = Long.MaxValue
            Dim fechaLimite As DateTime
            If body("desde") IsNot Nothing Then
                If Not DateTime.TryParse(body("desde").ToString(), Globalization.CultureInfo.InvariantCulture,
                                         Globalization.DateTimeStyles.AssumeUniversal Or Globalization.DateTimeStyles.AdjustToUniversal, fechaLimite) Then
                    Return ErrJson("desde invalido (YYYY-MM-DD)")
                End If
                desdeEpoch = New DateTimeOffset(fechaLimite).ToUnixTimeSeconds()
            End If
            If body("hasta") IsNot Nothing Then
                If Not DateTime.TryParse(body("hasta").ToString(), Globalization.CultureInfo.InvariantCulture,
                                         Globalization.DateTimeStyles.AssumeUniversal Or Globalization.DateTimeStyles.AdjustToUniversal, fechaLimite) Then
                    Return ErrJson("hasta invalido (YYYY-MM-DD)")
                End If
                hastaEpoch = New DateTimeOffset(fechaLimite).ToUnixTimeSeconds()
            End If

            Dim filas As New JArray()
            Dim cieTodos As New List(Of OperacionMultiplicador)()
            Dim disTodos As New List(Of OperacionMultiplicador)()
            Dim cieOperables As New List(Of OperacionMultiplicador)()
            Dim mercadosOperables = 0
            For Each m In CType(body("mercados"), JArray)
                Dim mercado = CType(m, JArray)
                Dim sym = mercado(0).ToString(), tf = mercado(1).ToString()
                Dim comision = If(mercado.Count > 2, CDbl(mercado(2)), 0.0)
                Dim mults = If(mercado.Count > 3 AndAlso TypeOf mercado(3) Is JArray,
                               CType(mercado(3), JArray).Select(Function(t) CInt(t)).ToList(), multsGlobal)
                Dim tpSl = tpSlGlobal
                If mercado.Count > 4 AndAlso TypeOf mercado(4) Is JArray Then
                    tpSl = CType(mercado(4), JArray).Select(Function(par) New Decimal() {CDec(par(0)), CDec(par(1))}).ToList()
                End If
                Dim maxVelas = If(mercado.Count > 5 AndAlso TypeOf mercado(5) Is JArray,
                                  CType(mercado(5), JArray).Select(Function(t) CInt(t)).ToList(), maxVelasGlobal)
                Dim velas = (Await ValidacionCompleta.CargarVelasAsync(sym, tf, fuente)) _
                    .Where(Function(v) v.Epoch >= desdeEpoch AndAlso v.Epoch < hastaEpoch).ToList()
                If velas.Count < ventana + 200 Then
                    filas.Add(New JObject From {{"symbol", sym}, {"timeframe", tf}, {"error", "Velas insuficientes: " & velas.Count}})
                    Continue For
                End If
                Dim tfSeg As Long = BacktestMotor.SegundosTimeframe(tf)
                ' Las senales solo dependen de estrategia, velas, ventana y confianza: se reutilizan
                ' entre llamadas (p. ej. la misma prueba con varias comisiones).
                Dim claveSenales = String.Join("|", est.Nombre, conf, ventana, fuente, sym, tf, velas.Count,
                                               velas(0).Epoch, velas(velas.Count - 1).Epoch)
                Dim r = Await Task.Run(Function()
                    Dim senales As List(Of SenalMultiplicador) = Nothing
                    If Not _cacheSenalesMult.TryGetValue(claveSenales, senales) Then
                        senales = MultiplicadorMotor.CalcularSenales(velas, est, ventana, conf)
                        If _cacheSenalesMult.Count > 500 Then _cacheSenalesMult.Clear()
                        _cacheSenalesMult(claveSenales) = senales
                    End If
                    Dim mejorCfg As MultiplicadorConfig = Nothing
                    Dim mejorOps As List(Of OperacionMultiplicador) = Nothing
                    Dim mejorPnl As Double = Double.MinValue
                    Dim mejorDesc As Integer = 0
                    Dim combos = 0
                    For Each mult In mults
                        For Each par In tpSl
                            For Each mv In maxVelas
                                Dim cfg As New MultiplicadorConfig With {
                                    .Multiplicador = mult, .Stake = 1D, .TakeProfit = par(0), .StopLoss = par(1),
                                    .MaxVelas = mv, .ComisionPct = comision}
                                Dim desc As Integer = 0
                                Dim ops = MultiplicadorMotor.Simular(velas, senales, cfg, tfSeg, desc)
                                combos += 1
                                Dim dis = ops.Where(Function(o) o.EpochEntrada < corteEpoch).ToList()
                                If dis.Count < minOps Then Continue For
                                Dim pnlDis = dis.Sum(Function(o) CDbl(o.Pnl))
                                If pnlDis > mejorPnl Then
                                    mejorPnl = pnlDis : mejorCfg = cfg : mejorOps = ops : mejorDesc = desc
                                End If
                            Next
                        Next
                    Next
                    Return Tuple.Create(senales.Count, combos, mejorCfg, mejorOps, mejorDesc)
                End Function)
                Dim fila As New JObject From {
                    {"symbol", sym}, {"timeframe", tf}, {"comision_pct", comision},
                    {"senales", r.Item1}, {"combinaciones_probadas", r.Item2}
                }
                If r.Item3 Is Nothing Then
                    fila("error") = String.Format("Ninguna combinacion llega a {0} operaciones en diseno.", minOps)
                    filas.Add(fila)
                    Continue For
                End If
                Dim c = r.Item3
                Dim disOps = r.Item4.Where(Function(o) o.EpochEntrada < corteEpoch).ToList()
                Dim cieOps = r.Item4.Where(Function(o) o.EpochEntrada >= corteEpoch).ToList()
                Dim operable = disOps.Sum(Function(o) CDbl(o.Pnl)) > 0
                fila("config_elegida") = New JObject From {
                    {"multiplicador", c.Multiplicador}, {"take_profit", c.TakeProfit}, {"stop_loss", c.StopLoss},
                    {"max_velas", c.MaxVelas}}
                fila("diseno_rentable") = operable
                fila("descartadas_por_hueco") = r.Item5
                fila("diseno") = MultiplicadorMotor.Resumen(disOps)
                fila("ciego") = MultiplicadorMotor.Resumen(cieOps)
                filas.Add(fila)
                disTodos.AddRange(disOps)
                cieTodos.AddRange(cieOps)
                If operable Then
                    mercadosOperables += 1
                    cieOperables.AddRange(cieOps)
                End If
            Next
            Return New JObject From {
                {"ok", True}, {"estrategia", est.Nombre}, {"min_confianza", conf}, {"fuente", fuente},
                {"desde_utc", If(desdeEpoch = Long.MinValue, "", DateTimeOffset.FromUnixTimeSeconds(desdeEpoch).ToString("yyyy-MM-dd"))},
                {"hasta_utc", If(hastaEpoch = Long.MaxValue, "", DateTimeOffset.FromUnixTimeSeconds(hastaEpoch).ToString("yyyy-MM-dd"))},
                {"corte_utc", corte.ToString("yyyy-MM-dd")}, {"stake", 1},
                {"total_diseno", MultiplicadorMotor.Resumen(disTodos)},
                {"total_ciego", MultiplicadorMotor.Resumen(cieTodos)},
                {"mercados_rentables_en_diseno", mercadosOperables},
                {"total_ciego_solo_rentables_en_diseno", MultiplicadorMotor.Resumen(cieOperables)},
                {"mercados", filas}
            }
        End Function

        ''' <summary>body: {estrategia, symbol, timeframe, duracion, confianza, payout, corte:"YYYY-MM-DD"}</summary>
        Private Async Function ValidarCompletoApi(body As JObject) As Task(Of JObject)
            If body Is Nothing Then Return ErrJson("Body: {estrategia, symbol, timeframe, duracion, confianza, payout, corte}")
            EstrategiasDinamicasRegistry.Inicializar()
            Dim est = EstrategiasDinamicasRegistry.GetByNombre(If(body("estrategia")?.ToString(), ""))
            If est Is Nothing Then Return ErrJson("Estrategia no encontrada.")
            Dim corte As DateTime
            If Not DateTime.TryParse(If(body("corte")?.ToString(), ""), Globalization.CultureInfo.InvariantCulture,
                                     Globalization.DateTimeStyles.AssumeUniversal Or Globalization.DateTimeStyles.AdjustToUniversal, corte) Then
                Return ErrJson("corte requerido (YYYY-MM-DD)")
            End If
            Try
                Dim symC = If(body("symbol")?.ToString(), ""), tfC = If(body("timeframe")?.ToString(), "")
                Dim durC = If(body("duracion") IsNot Nothing, CInt(body("duracion")), 2)
                Return Await ValidacionCompleta.EjecutarTodoAsync(est, symC, tfC, durC,
                    If(body("confianza") IsNot Nothing, CInt(body("confianza")), 55),
                    PayoutsReales.Resolver(body("payout"), symC, tfC, durC),
                    New DateTimeOffset(corte).ToUnixTimeSeconds(),
                    FuentesVelas.Normalizar(body("fuente")?.ToString()))
            Catch ex As Exception
                Return ErrJson(ex.Message)
            End Try
        End Function

        ''' <summary>body: {estrategia, symbol, timeframe, duracion, confianza, motivo, [payout (sin payout: el real medido), corte="2026-04-29"]}</summary>
        Private Async Function HipotesisApi(body As JObject) As Task(Of JObject)
            If body Is Nothing Then Return ErrJson("Body: {estrategia, symbol, timeframe, duracion, confianza, motivo, [payout], [corte]}")
            EstrategiasDinamicasRegistry.Inicializar()
            Dim est = EstrategiasDinamicasRegistry.GetByNombre(If(body("estrategia")?.ToString(), ""))
            If est Is Nothing Then Return ErrJson("Estrategia no encontrada.")
            Dim corte As DateTime
            If Not DateTime.TryParse(If(body("corte")?.ToString(), "2026-04-29"), Globalization.CultureInfo.InvariantCulture,
                                     Globalization.DateTimeStyles.AssumeUniversal Or Globalization.DateTimeStyles.AdjustToUniversal, corte) Then
                Return ErrJson("corte no valido (YYYY-MM-DD)")
            End If
            Dim sym = If(body("symbol")?.ToString(), ""), tf = If(body("timeframe")?.ToString(), "")
            Dim dur = If(body("duracion") IsNot Nothing, CInt(body("duracion")), 2)
            Try
                Return Await ValidacionCompleta.ProbarHipotesisAsync(est, sym, tf, dur,
                    If(body("confianza") IsNot Nothing, CInt(body("confianza")), 55),
                    PayoutsReales.Resolver(body("payout"), sym, tf, dur),
                    New DateTimeOffset(corte).ToUnixTimeSeconds(), If(body("motivo")?.ToString(), ""),
                    FuentesVelas.Normalizar(body("fuente")?.ToString()))
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

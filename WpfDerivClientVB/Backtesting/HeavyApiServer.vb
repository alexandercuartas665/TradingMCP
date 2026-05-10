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
            res.Headers.Add("Access-Control-Allow-Origin", "*")

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
                "DELETE /api/heavy/sesion/{id}"
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

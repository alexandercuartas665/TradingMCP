' ============================================================
' ModoAutonomo.vb
'
' Mantiene el sistema funcionando sin nadie delante (forward test
' de meses). Se activa con --autonomo:
'
'   1. Conexion: si la cuenta no esta conectada (y el servicio no
'      esta ya reintentando), conecta la DEMO; reintenta cada minuto
'      (al arrancar Windows, Docker y la red pueden tardar).
'   2. Sesiones: la primera vez que conecta, reanuda las sesiones
'      live activas MARCADAS para ello ([autonomo] en notas; se
'      marcan al iniciarlas con "autonomo": true). Las demas no.
'   3. Vigilancia (cada 5 min): avisa por WhatsApp si la conexion
'      lleva mas de 15 min caida o si una sesion lleva mas de 2 h
'      sin velas con el mercado abierto; avisa tambien al recuperarse.
'   4. Mantenimiento diario (22:10 UTC, pausa diaria de divisas):
'      actualiza todas las series de velas guardadas y hace una
'      copia de seguridad SIN credenciales (velas, sesiones y
'      operaciones; nunca clients ni client_deriv_credentials).
'
' Nunca conecta la cuenta REAL ni abre operaciones por su cuenta:
' solo reanuda lo que el usuario dejo en marcha.
' ============================================================
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB.Servidor

    Public Module ModoAutonomo

        Public Const HORA_MANTENIMIENTO_UTC As Integer = 22
        Public Const MINUTO_MANTENIMIENTO_UTC As Integer = 10
        Private Const MINUTOS_SIN_CONEXION_PARA_AVISAR As Integer = 15
        Private Const MINUTOS_ENTRE_VIGILANCIAS As Integer = 5
        Private Const SERIES_TABLAS As String = "-t public.candles -t public.backtest_sesiones -t public.backtest_operaciones -t public.trading_operations"

        Private ReadOnly _lock As New Object()
        Private _cts As CancellationTokenSource
        Private _sesionesReanudadas As Boolean
        Private _desconectadoDesdeUtc As DateTime? = Nothing
        Private _ultimoIntentoConexionUtc As DateTime = DateTime.MinValue
        Private _ultimoErrorConexion As String = ""
        Private ReadOnly _alertasActivas As New HashSet(Of String)()
        Private _ultimoMantenimientoFecha As Date = Date.MinValue
        Private _mantenimientoEnCurso As Integer = 0
        Private _estadoMantenimiento As JObject = New JObject From {{"ejecutado", False}}

        Public ReadOnly Property Activo As Boolean
            Get
                Return _cts IsNot Nothing AndAlso Not _cts.IsCancellationRequested
            End Get
        End Property

        ''' <summary>Arranca el bucle autonomo (idempotente).</summary>
        Public Sub Iniciar()
            SyncLock _lock
                If Activo Then Return
                _cts = New CancellationTokenSource()
                Dim ct = _cts.Token
                Task.Run(Function() BucleAsync(ct))
            End SyncLock
            AppLogger.Info("[Autonomo] Modo autonomo iniciado: conexion demo, reanudar sesiones, vigilancia y mantenimiento diario.")
        End Sub

        Private Async Function BucleAsync(ct As CancellationToken) As Task
            Dim proximaVigilancia = DateTime.UtcNow.AddMinutes(2)
            Do While Not ct.IsCancellationRequested
                Try
                    Await AsegurarConexionAsync()
                    Dim svc = DerivTradingService.Instance
                    If svc.EstaConectado AndAlso Not _sesionesReanudadas Then
                        _sesionesReanudadas = True
                        Dim n = Await LiveStrategiesController.GetInstance().ReanudarActivasAsync(soloMarcadasAutonomo:=True)
                        AppLogger.Info(String.Format("[Autonomo] Sesiones live reanudadas: {0}", n))
                    End If
                    If DateTime.UtcNow >= proximaVigilancia Then
                        proximaVigilancia = DateTime.UtcNow.AddMinutes(MINUTOS_ENTRE_VIGILANCIAS)
                        Await VigilarStreamingAsync()
                    End If
                    If TocaMantenimiento(DateTime.UtcNow, _ultimoMantenimientoFecha) AndAlso svc.EstaConectado Then
                        _ultimoMantenimientoFecha = DateTime.UtcNow.Date
                        Dim t = Task.Run(Function() EjecutarMantenimientoAsync(True, True, automatico:=True))
                    End If
                Catch ex As Exception
                    AppLogger.Warn("[Autonomo] Error en el bucle: " & ex.Message)
                End Try
                Try
                    Await Task.Delay(60000, ct)
                Catch ex As OperationCanceledException
                    Exit Do
                End Try
            Loop
        End Function

#Region "Conexion"
        Private Async Function AsegurarConexionAsync() As Task
            Dim svc = DerivTradingService.Instance
            If svc.EstaConectado Then
                If _desconectadoDesdeUtc.HasValue Then
                    Dim minutos = (DateTime.UtcNow - _desconectadoDesdeUtc.Value).TotalMinutes
                    _desconectadoDesdeUtc = Nothing
                    Await RecuperadoAsync("conexion", String.Format("Conexion con Deriv recuperada tras {0:F0} min.", minutos))
                End If
                Return
            End If
            If Not _desconectadoDesdeUtc.HasValue Then _desconectadoDesdeUtc = DateTime.UtcNow

            ' Si el servicio ya esta reintentando (reconexion automatica), no conectar en paralelo
            If Not svc.ReconexionEnCurso AndAlso (DateTime.UtcNow - _ultimoIntentoConexionUtc).TotalSeconds >= 55 Then
                _ultimoIntentoConexionUtc = DateTime.UtcNow
                Try
                    Dim r = Await svc.ConectarTradingAsync(True)
                    If r("ok") IsNot Nothing AndAlso CBool(r("ok")) Then
                        AppLogger.Info("[Autonomo] Cuenta demo conectada (" & If(r("cliente")?.ToString(), "") & ").")
                        _ultimoErrorConexion = ""
                    Else
                        _ultimoErrorConexion = If(r("error")?.ToString(), "sin detalle")
                        AppLogger.Warn("[Autonomo] No se pudo conectar la demo: " & _ultimoErrorConexion)
                    End If
                Catch ex As Exception
                    _ultimoErrorConexion = ex.Message
                    AppLogger.Warn("[Autonomo] Error conectando la demo: " & ex.Message)
                End Try
            End If

            If (DateTime.UtcNow - _desconectadoDesdeUtc.Value).TotalMinutes >= MINUTOS_SIN_CONEXION_PARA_AVISAR Then
                Await AlertarAsync("conexion", String.Format(
                    "Sin conexion con Deriv desde hace {0:F0} min. El forward test no recibe velas. Ultimo error: {1}",
                    (DateTime.UtcNow - _desconectadoDesdeUtc.Value).TotalMinutes, If(_ultimoErrorConexion = "", "reconectando", _ultimoErrorConexion)))
            End If
        End Function
#End Region

#Region "Vigilancia"
        Private Async Function VigilarStreamingAsync() As Task
            If Not DerivTradingService.Instance.EstaConectado Then Return   ' ya avisa la alerta de conexion
            For Each t In LiveStrategiesController.GetInstance().EstadoStreaming()
                Dim e = CType(t, JObject)
                Dim clave = "velas:" & e("symbol").ToString() & "/" & e("timeframe").ToString()
                If VelasAtrasadas(e) Then
                    Await AlertarAsync(clave, String.Format(
                        "{0} {1}: {2:F0} min sin velas nuevas con el mercado abierto. Revisa la app.",
                        e("symbol"), e("timeframe"), CDbl(e("minutos_desde_ultima_vela"))))
                Else
                    Await RecuperadoAsync(clave, String.Format("{0} {1}: vuelven a llegar velas.", e("symbol"), e("timeframe")))
                End If
            Next
        End Function

        ''' <summary>
        ''' True si una suscripcion con el mercado abierto lleva demasiado sin vela nueva:
        ''' el mayor entre 2 horas y 2 velas + 10 min (1h: 130 min; 4h: 490 min).
        ''' </summary>
        Friend Function VelasAtrasadas(e As JObject) As Boolean
            If e Is Nothing Then Return False
            If Not CBool(e("inicializada")) OrElse Not CBool(e("mercado_abierto")) Then Return False
            Dim minutos = CDbl(e("minutos_desde_ultima_vela"))
            If minutos < 0 Then Return False
            Dim limite = Math.Max(120.0, 2.0 * CDbl(e("granularidad")) / 60.0 + 10.0)
            Return minutos > limite
        End Function

        Private Async Function AlertarAsync(clave As String, mensaje As String) As Task
            SyncLock _alertasActivas
                If Not _alertasActivas.Add(clave) Then Return
            End SyncLock
            AppLogger.Warn("[Autonomo] ALERTA " & mensaje)
            Await EnviarWhatsAppAsync("ALERTA Traiding: " & mensaje)
        End Function

        Private Async Function RecuperadoAsync(clave As String, mensaje As String) As Task
            SyncLock _alertasActivas
                If Not _alertasActivas.Remove(clave) Then Return
            End SyncLock
            AppLogger.Info("[Autonomo] OK " & mensaje)
            Await EnviarWhatsAppAsync("OK Traiding: " & mensaje)
        End Function

        ''' <summary>WhatsApp al cliente conectado (o al primero de la BD si aun no hay conexion).</summary>
        Private Async Function EnviarWhatsAppAsync(texto As String) As Task
            Try
                Dim clientId = If(DerivTradingService.Instance.Cliente?.Id, 0)
                If clientId <= 0 Then
                    Dim clientes = Await New ClientRepository().GetAllAsync()
                    If clientes IsNot Nothing AndAlso clientes.Count > 0 Then clientId = clientes(0).Id
                End If
                If clientId <= 0 Then
                    AppLogger.Warn("[Autonomo] Sin cliente para enviar el aviso por WhatsApp.")
                    Return
                End If
                Dim r = Await New NotificacionService().EnviarMensajeClienteAsync(clientId, texto, preferirDemo:=True)
                If Not r.Exito Then AppLogger.Warn("[Autonomo] WhatsApp no enviado: " & r.Mensaje)
            Catch ex As Exception
                AppLogger.Warn("[Autonomo] Error enviando WhatsApp: " & ex.Message)
            End Try
        End Function
#End Region

#Region "Mantenimiento"
        Friend Function TocaMantenimiento(ahoraUtc As DateTime, ultimaFecha As Date) As Boolean
            If ultimaFecha >= ahoraUtc.Date Then Return False
            Dim hora = ahoraUtc.Date.AddHours(HORA_MANTENIMIENTO_UTC).AddMinutes(MINUTO_MANTENIMIENTO_UTC)
            Return ahoraUtc >= hora
        End Function

        ''' <summary>
        ''' Actualiza las velas y/o hace la copia de seguridad. Devuelve el resumen (tambien
        ''' queda en Estado()). Si ya hay un mantenimiento en curso, no hace nada.
        ''' </summary>
        Public Async Function EjecutarMantenimientoAsync(actualizarVelas As Boolean, respaldo As Boolean,
                                                         Optional automatico As Boolean = False) As Task(Of JObject)
            If Interlocked.Exchange(_mantenimientoEnCurso, 1) = 1 Then
                Return New JObject From {{"ok", False}, {"error", "Ya hay un mantenimiento en curso."}}
            End If
            Dim res As New JObject From {
                {"ok", True}, {"automatico", automatico}, {"inicio_utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")}, {"en_curso", True}
            }
            _estadoMantenimiento = res
            Try
                If actualizarVelas Then res("velas") = Await ActualizarVelasAsync()
                If actualizarVelas Then res("velas_externas") = Await ActualizarHistoricoExternoAsync()
                If respaldo Then res("respaldo") = Await RespaldarAsync()
            Catch ex As Exception
                res("ok") = False
                res("error") = ex.Message
            Finally
                res("en_curso") = False
                res("fin_utc") = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                Interlocked.Exchange(_mantenimientoEnCurso, 0)
            End Try
            Dim fallos As New List(Of String)()
            If res("velas") IsNot Nothing AndAlso CType(res("velas")("errores"), JArray).Count > 0 Then fallos.Add("velas")
            If res("respaldo") IsNot Nothing AndAlso Not CBool(res("respaldo")("ok")) Then fallos.Add("copia de seguridad")
            If res("error") IsNot Nothing Then fallos.Add(res("error").ToString())
            If fallos.Count > 0 Then
                res("ok") = False
                AppLogger.Warn("[Autonomo] Mantenimiento con errores: " & String.Join(", ", fallos))
                If automatico Then Await EnviarWhatsAppAsync("ALERTA Traiding: el mantenimiento diario fallo (" & String.Join(", ", fallos) & "). Revisa el log.")
            Else
                AppLogger.Info("[Autonomo] Mantenimiento completado.")
            End If
            Return res
        End Function

        ''' <summary>Descarga lo nuevo de cada serie guardada (con 3 velas de solape para corregir la ultima).</summary>
        Private Async Function ActualizarVelasAsync() As Task(Of JObject)
            Dim res As New JObject()
            Dim errores As New JArray()
            Dim detalle As New JArray()
            If Not DerivTradingService.Instance.EstaConectado Then
                errores.Add(DerivDatos.MENSAJE_SIN_CONEXION)
                res("errores") = errores
                Return res
            End If
            Dim repo = New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString())
            Dim series = Await repo.ListarSeriesAsync("Deriv")
            Dim totalNuevas = 0
            For Each s In series
                Dim gran = Backtesting.BacktestMotor.SegundosTimeframe(s.Item2)
                If gran <= 0 Then Continue For
                Dim desde = DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, s.Item3 - 3L * gran)).UtcDateTime
                Dim ultimo As DownloadProgress = Nothing
                Dim descargador As New HistoricalDownloader(repo) With {.ActualizarExistentes = True}
                Try
                    Await descargador.DownloadAsync("Deriv", s.Item1, s.Item2, desde, DateTime.UtcNow.Date,
                                                   Sub(p) ultimo = p)
                Catch ex As Exception
                    errores.Add(s.Item1 & " " & s.Item2 & ": " & ex.Message)
                    Continue For
                End Try
                Dim guardadas = If(ultimo Is Nothing, 0, ultimo.TotalSaved)
                totalNuevas += guardadas
                If ultimo IsNot Nothing AndAlso ultimo.HasError Then
                    ' Mercado cerrado o sin datos nuevos no es un fallo
                    Dim msg = If(ultimo.StatusMsg, "")
                    If msg.Contains("InvalidSymbol") Then
                        ' El simbolo ya no existe en Deriv: no es un fallo del mantenimiento
                        detalle.Add(New JObject From {{"serie", s.Item1 & " " & s.Item2}, {"velas", 0}, {"nota", "simbolo retirado por Deriv"}})
                        Continue For
                    End If
                    If Not (msg.Contains("MarketIsClosed") OrElse msg.Contains("closed")) Then
                        errores.Add(s.Item1 & " " & s.Item2 & ": " & ultimo.StatusMsg)
                    End If
                End If
                detalle.Add(New JObject From {{"serie", s.Item1 & " " & s.Item2}, {"velas", guardadas}})
            Next
            res("series") = series.Count
            res("velas_descargadas") = totalNuevas
            res("detalle") = detalle
            res("errores") = errores
            Return res
        End Function

        ''' <summary>Carpeta de copias (config RespaldosCarpeta o %LOCALAPPDATA%\WpfDerivClientVB\respaldos).</summary>
        ''' <summary>
        ''' Completa el historico externo (Dukascopy) hasta ayer. Si el servidor de Dukascopy
        ''' falla no se alerta por WhatsApp (son datos de investigacion, se reintentan la noche
        ''' siguiente); queda en el log y en el resumen del mantenimiento.
        ''' </summary>
        Private Async Function ActualizarHistoricoExternoAsync() As Task(Of JObject)
            Try
                Dim r = Await DukascopyDescarga.Instancia.ActualizarTodoAsync()
                If r("ok") IsNot Nothing AndAlso Not CBool(r("ok")) Then AppLogger.Warn("[Autonomo] Historico externo incompleto: " & r.ToString(Newtonsoft.Json.Formatting.None))
                Return r
            Catch ex As Exception
                AppLogger.Warn("[Autonomo] Historico externo: " & ex.Message)
                Return New JObject From {{"ok", False}, {"error", ex.Message}}
            End Try
        End Function

        Public Function CarpetaRespaldos() As String
            Dim cfg = ConfigManager.LoadDbConfig()
            If Not String.IsNullOrWhiteSpace(cfg.RespaldosCarpeta) Then Return cfg.RespaldosCarpeta
            Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfDerivClientVB", "respaldos")
        End Function

        ''' <summary>
        ''' pg_dump (formato custom) de velas, sesiones y operaciones, sin las tablas con credenciales.
        ''' Se genera dentro del contenedor, se copia fuera, se verifica con pg_restore -l y se
        ''' borran las copias antiguas (se conservan RespaldosConservar).
        ''' </summary>
        Private Async Function RespaldarAsync() As Task(Of JObject)
            Dim cfg = ConfigManager.LoadDbConfig()
            Dim contenedor = cfg.DockerContenedorPostgres
            Dim carpeta = CarpetaRespaldos()
            Directory.CreateDirectory(carpeta)
            Dim nombre = "traiding_datos_" & DateTime.UtcNow.ToString("yyyyMMdd_HHmm") & ".dump"
            Dim tmp = "/tmp/" & nombre
            Dim destino = Path.Combine(carpeta, nombre)
            Dim res As New JObject From {{"ok", False}, {"archivo", destino}}

            Dim dump = Await EjecutarAsync("docker", String.Format("exec {0} pg_dump -U {1} -d {2} -Fc {3} -f {4}",
                                                                   contenedor, cfg.Username, cfg.Database, SERIES_TABLAS, tmp), 900000)
            If dump.Item1 <> 0 Then
                res("error") = "pg_dump: " & dump.Item3
                Return res
            End If
            Dim lista = Await EjecutarAsync("docker", String.Format("exec {0} pg_restore -l {1}", contenedor, tmp), 120000)
            Dim copia = Await EjecutarAsync("docker", String.Format("cp {0}:{1} ""{2}""", contenedor, tmp, destino), 600000)
            Await EjecutarAsync("docker", String.Format("exec {0} rm -f {1}", contenedor, tmp), 60000)
            If lista.Item1 <> 0 Then
                res("error") = "La copia no se pudo verificar: " & lista.Item3
                Return res
            End If
            If copia.Item1 <> 0 OrElse Not File.Exists(destino) Then
                res("error") = "docker cp: " & copia.Item3
                Return res
            End If
            Dim tablas = lista.Item2.Split({vbLf}, StringSplitOptions.RemoveEmptyEntries).Count(Function(l) l.Contains("TABLE DATA"))
            res("ok") = tablas >= 4
            If tablas < 4 Then res("error") = "La copia solo contiene " & tablas & " tablas."
            res("tablas") = tablas
            res("tamano_mb") = Math.Round(New FileInfo(destino).Length / 1048576.0, 1)

            Dim borrados As New JArray()
            Dim existentes = Directory.GetFiles(carpeta, "traiding_datos_*.dump").Select(Function(f) Path.GetFileName(f))
            For Each viejo In RespaldosABorrar(existentes, Math.Max(1, cfg.RespaldosConservar))
                Try
                    File.Delete(Path.Combine(carpeta, viejo))
                    borrados.Add(viejo)
                Catch ex As Exception
                    AppLogger.Warn("[Autonomo] No se pudo borrar la copia antigua " & viejo & ": " & ex.Message)
                End Try
            Next
            res("borrados") = borrados
            Return res
        End Function

        ''' <summary>Las copias mas antiguas que sobran (el nombre lleva la fecha: orden alfabetico = cronologico).</summary>
        Friend Function RespaldosABorrar(nombres As IEnumerable(Of String), conservar As Integer) As List(Of String)
            Return nombres.OrderByDescending(Function(n) n, StringComparer.Ordinal).Skip(conservar).ToList()
        End Function

        ''' <summary>Ejecuta un proceso y devuelve (codigo de salida, stdout, stderr).</summary>
        Private Async Function EjecutarAsync(archivo As String, argumentos As String, timeoutMs As Integer) As Task(Of Tuple(Of Integer, String, String))
            Return Await Task.Run(Function()
                Dim psi As New ProcessStartInfo(archivo, argumentos) With {
                    .UseShellExecute = False, .RedirectStandardOutput = True, .RedirectStandardError = True, .CreateNoWindow = True}
                Using p = Process.Start(psi)
                    Dim salida = p.StandardOutput.ReadToEndAsync()
                    Dim errores = p.StandardError.ReadToEndAsync()
                    If Not p.WaitForExit(timeoutMs) Then
                        Try
                            p.Kill()
                        Catch
                        End Try
                        Return Tuple.Create(-1, "", "Tiempo agotado.")
                    End If
                    Return Tuple.Create(p.ExitCode, salida.Result, errores.Result)
                End Using
            End Function)
        End Function
#End Region

        ''' <summary>Estado completo para /api/heavy/salud.</summary>
        Public Function Estado() As JObject
            Dim svc = DerivTradingService.Instance
            Dim alertas As JArray
            SyncLock _alertasActivas
                alertas = New JArray(_alertasActivas.ToArray())
            End SyncLock
            Return New JObject From {
                {"autonomo", Activo},
                {"conectado", svc.EstaConectado}, {"demo", svc.EsDemo}, {"reconectando", svc.ReconexionEnCurso},
                {"desconectado_desde_utc", If(_desconectadoDesdeUtc.HasValue, _desconectadoDesdeUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ"), "")},
                {"ultimo_error_conexion", _ultimoErrorConexion},
                {"sesiones_reanudadas", _sesionesReanudadas},
                {"sesiones_live", LiveStrategiesController.GetInstance().Count},
                {"streaming", LiveStrategiesController.GetInstance().EstadoStreaming()},
                {"alertas_activas", alertas},
                {"mantenimiento", _estadoMantenimiento},
                {"proximo_mantenimiento_utc", String.Format("{0:00}:{1:00} UTC", HORA_MANTENIMIENTO_UTC, MINUTO_MANTENIMIENTO_UTC)},
                {"carpeta_respaldos", CarpetaRespaldos()}
            }
        End Function

    End Module

End Namespace

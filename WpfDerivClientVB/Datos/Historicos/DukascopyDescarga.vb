Imports System.Collections.Generic
Imports System.Linq
Imports System.Net
Imports System.Net.Http
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB

    ''' <summary>
    ''' Descarga historico largo (divisas, metales e indices desde ~2003) de los archivos
    ''' publicos de Dukascopy y lo guarda en candles con platform = "Dukascopy", con el
    ''' nombre de simbolo de Deriv (frxEURUSD...) para poder comparar. No se mezcla con las
    ''' velas de Deriv: cada prueba elige su fuente.
    '''
    ''' - 1h, 4h y 1d salen del archivo mensual de velas de 1 hora (un archivo por mes).
    ''' - 1m, 5m, 15m y 30m salen del archivo diario de velas de 1 minuto (uno por dia).
    ''' - Solo dias completos (hasta ayer UTC). Lo ya guardado no se vuelve a bajar, salvo los
    '''   ultimos dias, que se repasan por si el archivo llego incompleto.
    ''' - El servidor de Dukascopy corta conexiones o responde 503 a menudo: se reintenta con
    '''   esperas crecientes y lo que falle queda listado para volver a intentarlo.
    ''' </summary>
    Public Class DukascopyDescarga

        Public Shared ReadOnly Property Instancia As New DukascopyDescarga()
        Public Shared ReadOnly TF_POR_DEFECTO As String() = {"1h", "4h", "1d"}

        Private Shared ReadOnly Http As HttpClient = CrearHttp()
        Private ReadOnly _bloqueo As New Object()
        Private _cts As CancellationTokenSource
        Private _activo As Integer
        Private _estado As New JObject From {{"activo", False}, {"mensaje", "Sin descargas en esta sesion."}}
        ''' <summary>True cuando https fallo en esta descarga y se sigue por http.</summary>
        Private _soloHttp As Boolean

        ''' <summary>Pausa entre archivos para no saturar el servidor.</summary>
        Public Property PausaEntreArchivosMs As Integer = 200
        Public Property Reintentos As Integer = 8

        Private Shared Function CrearHttp() As HttpClient
            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol Or SecurityProtocolType.Tls12
            Dim h As New HttpClient() With {.Timeout = TimeSpan.FromSeconds(45)}
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)")
            Return h
        End Function

        Public ReadOnly Property EstaDescargando As Boolean
            Get
                Return Volatile.Read(_activo) = 1
            End Get
        End Property

        Public Function Estado() As JObject
            SyncLock _bloqueo
                Return CType(_estado.DeepClone(), JObject)
            End SyncLock
        End Function

        Public Sub Detener()
            _cts?.Cancel()
        End Sub

        Private Sub Actualizar(accion As Action(Of JObject))
            SyncLock _bloqueo
                accion(_estado)
            End SyncLock
        End Sub

        ''' <summary>Valida y lanza la descarga en segundo plano. Se sigue con Estado().</summary>
        Public Function Iniciar(simbolos As IList(Of String), temporalidades As IList(Of String),
                                desde As Date, hasta As Date) As JObject
            Dim tfs = If(temporalidades Is Nothing OrElse temporalidades.Count = 0, TF_POR_DEFECTO.ToList(), temporalidades.ToList())
            Dim malos = tfs.Where(Function(t) DukascopyFormato.SegundosTf(t) = 0).ToList()
            If malos.Count > 0 Then Return Err("Temporalidades no soportadas: " & String.Join(", ", malos) & " (use 1m, 5m, 15m, 30m, 1h, 4h, 1d)")
            If simbolos Is Nothing OrElse simbolos.Count = 0 Then Return Err("Indica al menos un simbolo (p. ej. frxEURUSD).")
            Dim sinFuente = simbolos.Where(Function(s) DukascopyFormato.SimboloDukascopy(s) Is Nothing).ToList()
            If sinFuente.Count > 0 Then Return Err("Dukascopy no tiene: " & String.Join(", ", sinFuente) & ". Solo divisas (frx...), metales e indices OTC; los sinteticos de Deriv (R_100...) no existen fuera de Deriv.")
            If desde > hasta Then Return Err("'desde' es posterior a 'hasta'.")
            If Interlocked.CompareExchange(_activo, 1, 0) = 1 Then Return Err("Ya hay una descarga de Dukascopy en curso.")
            _cts = New CancellationTokenSource()
            Dim ct = _cts.Token
            Tareas.EnSegundoPlano(Task.Run(Async Function()
                                                Try
                                                    Await DescargarAsync(simbolos.ToList(), tfs, desde, hasta, ct)
                                                Finally
                                                    Interlocked.Exchange(_activo, 0)
                                                End Try
                                            End Function), "Descarga Dukascopy")
            Return New JObject From {
                {"ok", True},
                {"mensaje", String.Format("Descarga iniciada: {0} [{1}] {2:yyyy-MM-dd} -> {3:yyyy-MM-dd}. Sigue el avance en /api/heavy/historico-externo/estado.",
                                          String.Join(", ", simbolos), String.Join(", ", tfs), desde, hasta)}
            }
        End Function

        Private Shared Function Err(msg As String) As JObject
            Return New JObject From {{"ok", False}, {"error", msg}}
        End Function

        ''' <summary>
        ''' Descarga sin lanzar en segundo plano (la usa Iniciar y el mantenimiento nocturno).
        ''' Devuelve el resumen final, que tambien queda en Estado().
        ''' </summary>
        Public Async Function DescargarAsync(simbolos As List(Of String), tfs As List(Of String),
                                             desde As Date, hasta As Date, ct As CancellationToken) As Task(Of JObject)
            Dim hoy = DateTime.UtcNow.Date
            Dim hastaEx = If(hasta.Date.AddDays(1) < hoy, hasta.Date.AddDays(1), hoy)
            Dim porSerie As New JObject()
            Dim fallidos As New JArray()
            Actualizar(Sub(e)
                           e.RemoveAll()
                           e("activo") = True
                           e("inicio_utc") = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                           e("simbolos") = New JArray(simbolos)
                           e("temporalidades") = New JArray(tfs)
                           e("desde") = desde.ToString("yyyy-MM-dd")
                           e("hasta") = hastaEx.AddDays(-1).ToString("yyyy-MM-dd")
                           e("archivos_bajados") = 0
                           e("archivos_sin_datos") = 0
                           e("archivos_omitidos_ya_guardados") = 0
                           e("velas_guardadas") = 0
                           e("por_serie") = porSerie
                           e("archivos_fallidos") = fallidos
                           e("mensaje") = "Empezando..."
                       End Sub)
            Dim repo As New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString())
            Dim cancelado = False
            _soloHttp = False   ' cada descarga vuelve a probar https primero
            Actualizar(Sub(e) e("protocolo") = "https")
            Try
                For Each sym In simbolos
                    Dim tfsHoras = tfs.Where(Function(t) Not DukascopyFormato.UsaMinutos(t)).ToList()
                    Dim tfsMin = tfs.Where(Function(t) DukascopyFormato.UsaMinutos(t)).ToList()
                    If tfsHoras.Count > 0 Then Await DescargarHorasAsync(repo, sym, tfsHoras, desde.Date, hastaEx, porSerie, fallidos, ct)
                    If tfsMin.Count > 0 Then Await DescargarMinutosAsync(repo, sym, tfsMin, desde.Date, hastaEx, porSerie, fallidos, ct)
                Next
            Catch ex As OperationCanceledException
                cancelado = True
            Catch ex As Exception
                Actualizar(Sub(e) e("error") = ex.Message)
                AppLogger.Error("[Dukascopy] Descarga interrumpida", ex)
            End Try
            Actualizar(Sub(e)
                           e("activo") = False
                           e("fin_utc") = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                           Dim nf = CType(e("archivos_fallidos"), JArray).Count
                           Dim msg = If(cancelado, "Detenida por el usuario. ", If(e("error") IsNot Nothing, "Interrumpida por un error: " & e("error").ToString() & " ", "Terminada. "))
                           msg &= String.Format("{0} velas guardadas.", e("velas_guardadas"))
                           If nf > 0 Then msg &= String.Format(" {0} archivo(s) no se pudieron bajar (el servidor no respondio): vuelve a lanzar la descarga para completarlos; lo ya guardado no se repite.", nf)
                           e("mensaje") = msg
                       End Sub)
            AppLogger.Info("[Dukascopy] " & Estado()("mensaje").ToString())
            Return Estado()
        End Function

        Private Async Function DescargarHorasAsync(repo As CandleRepository, sym As String, tfs As List(Of String),
                                                   desde As Date, hastaEx As Date, porSerie As JObject, fallidos As JArray,
                                                   ct As CancellationToken) As Task
            Dim sd = DukascopyFormato.SimboloDukascopy(sym)
            Dim div = DukascopyFormato.Divisor(sd)
            Dim hoy = DateTime.UtcNow.Date
            Dim mesActual = New Date(hoy.Year, hoy.Month, 1)
            ' Meses ya guardados en todas las temporalidades pedidas (los dos ultimos se repasan siempre)
            Dim guardados As HashSet(Of String) = Nothing
            For Each tf In tfs
                Dim s = Await repo.PeriodosConDatosAsync(FuentesVelas.DUKASCOPY, sym, tf, "YYYY-MM")
                If guardados Is Nothing Then guardados = s Else guardados.IntersectWith(s)
            Next
            ' Siempre meses completos (aunque 'desde' caiga a mitad de mes): asi un mes guardado esta entero
            Dim hastaEp = DukascopyFormato.EpochDe(hastaEx)
            For Each mes In DukascopyFormato.Meses(desde, hastaEx)
                ct.ThrowIfCancellationRequested()
                Dim clave = mes.ToString("yyyy-MM")
                Actualizar(Sub(e) e("mensaje") = String.Format("{0} horas {1}", sym, clave))
                If mes < mesActual.AddMonths(-1) AndAlso guardados.Contains(clave) Then
                    Actualizar(Sub(e) e("archivos_omitidos_ya_guardados") = CInt(e("archivos_omitidos_ya_guardados")) + 1)
                    Continue For
                End If
                Dim velasH As List(Of CandleModel)
                If mes >= mesActual Then
                    ' El archivo mensual del mes en curso aun no existe: se arma con los diarios de minutos
                    ' (solo desde 'desde': en la actualizacion nocturna son pocos dias)
                    velasH = DukascopyFormato.Agregar(Await MinutosDeRangoAsync(sd, div, If(desde > mes, desde, mes), If(mes.AddMonths(1) < hastaEx, mes.AddMonths(1), hastaEx), fallidos, ct), 3600)
                Else
                    Dim datos = Await BajarAsync(DukascopyFormato.UrlHoras(sd, mes.Year, mes.Month), fallidos, ct)
                    If datos Is Nothing AndAlso mes >= mesActual.AddMonths(-1) Then
                        velasH = DukascopyFormato.Agregar(Await MinutosDeRangoAsync(sd, div, mes, mes.AddMonths(1), fallidos, ct), 3600)
                    Else
                        velasH = DukascopyFormato.LeerVelas(datos, DukascopyFormato.EpochDe(mes), div)
                    End If
                End If
                velasH = velasH.Where(Function(v) v.Epoch < hastaEp).ToList()
                For Each tf In tfs
                    Dim lote = If(tf = "1h", velasH, DukascopyFormato.Agregar(velasH, DukascopyFormato.SegundosTf(tf)))
                    Await GuardarAsync(repo, sym, tf, lote, porSerie)
                Next
                Await Task.Delay(PausaEntreArchivosMs, ct)
            Next
        End Function

        Private Async Function DescargarMinutosAsync(repo As CandleRepository, sym As String, tfs As List(Of String),
                                                     desde As Date, hastaEx As Date, porSerie As JObject, fallidos As JArray,
                                                     ct As CancellationToken) As Task
            Dim sd = DukascopyFormato.SimboloDukascopy(sym)
            Dim div = DukascopyFormato.Divisor(sd)
            Dim hoy = DateTime.UtcNow.Date
            Dim guardados As HashSet(Of String) = Nothing
            For Each tf In tfs
                Dim s = Await repo.PeriodosConDatosAsync(FuentesVelas.DUKASCOPY, sym, tf, "YYYY-MM-DD")
                If guardados Is Nothing Then guardados = s Else guardados.IntersectWith(s)
            Next
            Dim pendientes As New List(Of CandleModel)()
            For Each dia In DukascopyFormato.Dias(desde, hastaEx)
                ct.ThrowIfCancellationRequested()
                Dim clave = dia.ToString("yyyy-MM-dd")
                If dia < hoy.AddDays(-3) AndAlso guardados.Contains(clave) Then
                    Actualizar(Sub(e) e("archivos_omitidos_ya_guardados") = CInt(e("archivos_omitidos_ya_guardados")) + 1)
                    Continue For
                End If
                Actualizar(Sub(e) e("mensaje") = String.Format("{0} minutos {1}", sym, clave))
                pendientes.AddRange(Await MinutosDeRangoAsync(sd, div, dia, dia.AddDays(1), fallidos, ct))
                ' Guardar por tandas (cada ~2 semanas de minutos) para no acumular memoria
                If pendientes.Count >= 15000 Then
                    Await GuardarMinutosAsync(repo, sym, tfs, pendientes, porSerie)
                    pendientes.Clear()
                End If
            Next
            Await GuardarMinutosAsync(repo, sym, tfs, pendientes, porSerie)
        End Function

        Private Async Function GuardarMinutosAsync(repo As CandleRepository, sym As String, tfs As List(Of String),
                                                   minutos As List(Of CandleModel), porSerie As JObject) As Task
            If minutos.Count = 0 Then Return
            For Each tf In tfs
                Dim lote = If(tf = "1m", minutos, DukascopyFormato.Agregar(minutos, DukascopyFormato.SegundosTf(tf)))
                Await GuardarAsync(repo, sym, tf, lote, porSerie)
            Next
        End Function

        ''' <summary>Velas de 1 minuto de los dias [desde, hastaEx).</summary>
        Private Async Function MinutosDeRangoAsync(sd As String, div As Decimal, desde As Date, hastaEx As Date,
                                                   fallidos As JArray, ct As CancellationToken) As Task(Of List(Of CandleModel))
            Dim res As New List(Of CandleModel)()
            For Each dia In DukascopyFormato.Dias(desde, hastaEx)
                Dim datos = Await BajarAsync(DukascopyFormato.UrlMinutos(sd, dia), fallidos, ct)
                If datos IsNot Nothing Then res.AddRange(DukascopyFormato.LeerVelas(datos, DukascopyFormato.EpochDe(dia), div))
                Await Task.Delay(PausaEntreArchivosMs, ct)
            Next
            Return res
        End Function

        Private Async Function GuardarAsync(repo As CandleRepository, sym As String, tf As String,
                                            lote As List(Of CandleModel), porSerie As JObject) As Task
            If lote.Count = 0 Then Return
            Dim n = Await repo.GuardarLoteAsync(FuentesVelas.DUKASCOPY, sym, tf, lote)
            Actualizar(Sub(e)
                           e("velas_guardadas") = CInt(e("velas_guardadas")) + n
                           Dim clave = sym & " " & tf
                           porSerie(clave) = If(porSerie(clave) Is Nothing, 0, CInt(porSerie(clave))) + n
                       End Sub)
        End Function

        ''' <summary>
        ''' Baja un archivo y lo devuelve YA DESCOMPRIMIDO. 404 o archivo vacio = no hay datos
        ''' ese periodo (Nothing). Cortes de conexion, 503, archivos que no se pueden
        ''' descomprimir y similares se reintentan; si nunca responde, queda en 'fallidos'.
        ''' El acceso https de Dukascopy a veces se satura mientras el http responde bien: tras
        ''' dos fallos seguidos por https, el resto de la descarga va por http (son precios
        ''' publicos; cada archivo se valida al descomprimirlo).
        ''' </summary>
        Private Async Function BajarAsync(url As String, fallidos As JArray, ct As CancellationToken) As Task(Of Byte())
            Dim ultimo = ""
            Dim fallosHttps = 0
            For intento = 1 To Math.Max(1, Reintentos)
                ct.ThrowIfCancellationRequested()
                Dim u = If(_soloHttp, ConHttp(url), url)
                Try
                    Using resp = Await Http.GetAsync(u, ct)
                        If resp.StatusCode = HttpStatusCode.NotFound Then
                            Actualizar(Sub(e) e("archivos_sin_datos") = CInt(e("archivos_sin_datos")) + 1)
                            Return Nothing
                        End If
                        If resp.IsSuccessStatusCode Then
                            Dim b = Await resp.Content.ReadAsByteArrayAsync()
                            If b.Length = 0 Then
                                Actualizar(Sub(e) e("archivos_sin_datos") = CInt(e("archivos_sin_datos")) + 1)
                                Return Nothing
                            End If
                            Dim datos = DukascopyFormato.Descomprimir(b)   ' si no es un .bi5 valido, lanza y se reintenta
                            Actualizar(Sub(e) e("archivos_bajados") = CInt(e("archivos_bajados")) + 1)
                            Return datos
                        End If
                        ultimo = "HTTP " & CInt(resp.StatusCode).ToString()
                    End Using
                Catch ex As OperationCanceledException When ct.IsCancellationRequested
                    Throw
                Catch ex As Exception
                    ' Timeout, conexion cortada o archivo corrupto: se reintenta
                    ultimo = ex.GetBaseException().Message
                End Try
                If Not _soloHttp Then
                    fallosHttps += 1
                    If fallosHttps >= 2 Then
                        _soloHttp = True
                        Actualizar(Sub(e) e("protocolo") = "http (el acceso https de Dukascopy no responde)")
                        AppLogger.Warn("[Dukascopy] https no responde (" & ultimo & "); se sigue por http.")
                        Continue For
                    End If
                End If
                Await Task.Delay(EsperaReintento(intento), ct)
            Next
            Actualizar(Sub(e)
                           Dim f = CType(e("archivos_fallidos"), JArray)
                           If f.Count < 200 Then f.Add(url.Replace(DukascopyFormato.URL_BASE, "") & " (" & ultimo & ")")
                       End Sub)
            Return Nothing
        End Function

        Friend Shared Function ConHttp(url As String) As String
            Return If(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase), "http://" & url.Substring(8), url)
        End Function

        ''' <summary>Espera antes del reintento n: 2, 4, 8, 16 y luego 30 segundos.</summary>
        Friend Shared Function EsperaReintento(intento As Integer) As Integer
            Return Math.Min(30000, 1000 * CInt(Math.Pow(2, Math.Max(1, intento))))
        End Function

        ''' <summary>
        ''' Actualizacion nocturna: cada serie de Dukascopy ya guardada se completa desde su
        ''' ultima vela (menos un dia) hasta ayer. Si no hay ninguna, no hace nada.
        ''' </summary>
        Public Async Function ActualizarTodoAsync(Optional ct As CancellationToken = Nothing) As Task(Of JObject)
            Dim repo As New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString())
            Dim series = Await repo.ListarSeriesAsync(FuentesVelas.DUKASCOPY)
            If series.Count = 0 Then Return New JObject From {{"ok", True}, {"series", 0}, {"mensaje", "Sin historico externo guardado."}}
            If Interlocked.CompareExchange(_activo, 1, 0) = 1 Then Return New JObject From {{"ok", False}, {"error", "Ya hay una descarga de Dukascopy en curso."}}
            Dim total = 0
            Dim fallidos = 0
            Try
                For Each grupo In series.GroupBy(Function(s) s.Item1)
                    Dim desde = DateTimeOffset.FromUnixTimeSeconds(grupo.Min(Function(s) s.Item3)).UtcDateTime.Date.AddDays(-1)
                    Dim r = Await DescargarAsync(New List(Of String) From {grupo.Key}, grupo.Select(Function(s) s.Item2).ToList(),
                                                 desde, DateTime.UtcNow.Date, ct)
                    total += CInt(r("velas_guardadas"))
                    fallidos += CType(r("archivos_fallidos"), JArray).Count
                Next
            Finally
                Interlocked.Exchange(_activo, 0)
            End Try
            Return New JObject From {{"ok", fallidos = 0}, {"series", series.Count}, {"velas_guardadas", total}, {"archivos_fallidos", fallidos}}
        End Function

        ''' <summary>
        ''' Compara Dukascopy con Deriv en las velas que coinciden en hora: cuanto se parecen
        ''' los precios y si las velas van en la misma direccion (lo que decide un CALL/PUT).
        ''' </summary>
        Public Shared Async Function CompararAsync(symbol As String, timeframe As String) As Task(Of JObject)
            Dim repo As New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString())
            Dim ahora = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            Dim deriv = Await repo.GetCandlesRangoAsync(FuentesVelas.DERIV, symbol, timeframe, 0, ahora)
            Dim duka = Await repo.GetCandlesRangoAsync(FuentesVelas.DUKASCOPY, symbol, timeframe, 0, ahora)
            Return Comparar(deriv, duka, symbol, timeframe)
        End Function

        ''' <summary>Comparacion pura (probada en pruebas).</summary>
        Public Shared Function Comparar(deriv As List(Of CandleModel), duka As List(Of CandleModel),
                                        symbol As String, timeframe As String) As JObject
            Dim mapa = duka.GroupBy(Function(v) v.Epoch).ToDictionary(Function(g) g.Key, Function(g) g.First())
            Dim n = 0, mismaDir = 0, ambasMovidas = 0
            Dim sumaDif As Double = 0
            Dim primero As Long = 0, ultimo As Long = 0
            For Each d In deriv
                Dim k As CandleModel = Nothing
                If Not mapa.TryGetValue(d.Epoch, k) OrElse d.ClosePrice = 0 Then Continue For
                n += 1
                If primero = 0 Then primero = d.Epoch
                ultimo = d.Epoch
                sumaDif += Math.Abs(CDbl(k.ClosePrice - d.ClosePrice) / CDbl(d.ClosePrice))
                Dim dirD = Math.Sign(d.ClosePrice - d.OpenPrice), dirK = Math.Sign(k.ClosePrice - k.OpenPrice)
                If dirD <> 0 AndAlso dirK <> 0 Then
                    ambasMovidas += 1
                    If dirD = dirK Then mismaDir += 1
                End If
            Next
            Dim fecha = Function(ep As Long) If(ep = 0, "", DateTimeOffset.FromUnixTimeSeconds(ep).UtcDateTime.ToString("yyyy-MM-dd"))
            Return New JObject From {
                {"ok", True}, {"symbol", symbol}, {"timeframe", timeframe},
                {"velas_deriv", deriv.Count}, {"velas_dukascopy", duka.Count},
                {"velas_comparadas", n}, {"desde", fecha(primero)}, {"hasta", fecha(ultimo)},
                {"diferencia_media_cierre_pct", If(n > 0, Math.Round(sumaDif / n * 100, 4), 0.0)},
                {"misma_direccion_pct", If(ambasMovidas > 0, Math.Round(mismaDir / CDbl(ambasMovidas) * 100, 2), 0.0)}
            }
        End Function
    End Class
End Namespace

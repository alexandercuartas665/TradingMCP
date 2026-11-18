Imports System.Collections.Generic
Imports System.Linq
Imports System.Net
Imports System.Net.Http
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB

    ''' <summary>
    ''' Descarga historico de las APIs publicas de Bitstamp, Binance y Yahoo y lo guarda en
    ''' candles con su propio platform (nunca se mezcla con Deriv), con el nombre de simbolo
    ''' de Deriv (cryBTCUSD, OTC_NDX...) para poder comparar.
    '''
    ''' - Bitstamp y Binance: velas de 1h pedidas mes a mes (una peticion por mes); 4h y 1d se
    '''   arman con ellas, alineadas a las 00 UTC como Deriv. Los meses ya guardados no se
    '''   vuelven a pedir, salvo los dos ultimos.
    ''' - Yahoo: solo velas diarias de indices de contado, una peticion para todo el rango.
    ''' - Solo dias completos (hasta ayer UTC). Errores de red, 429 y 5xx se reintentan con
    '''   esperas crecientes; lo que no responda queda listado para volver a lanzar.
    ''' </summary>
    Public Class DescargaHttpExterna
        Implements IDescargaHistorica

        Public Shared ReadOnly Property Bitstamp As New DescargaHttpExterna(FuentesVelas.BITSTAMP)
        Public Shared ReadOnly Property Binance As New DescargaHttpExterna(FuentesVelas.BINANCE)
        Public Shared ReadOnly Property Yahoo As New DescargaHttpExterna(FuentesVelas.YAHOO)

        Private Shared ReadOnly BasesBinance As String() = {"https://data-api.binance.vision", "https://api.binance.com"}
        Private Shared ReadOnly Http As HttpClient = CrearHttp()

        Private ReadOnly _fuente As String
        Private ReadOnly _bloqueo As New Object()
        Private _cts As CancellationTokenSource
        Private _activo As Integer
        Private _estado As New JObject From {{"activo", False}, {"mensaje", "Sin descargas en esta sesion."}}

        Public Property PausaEntrePeticionesMs As Integer = 300
        Public Property Reintentos As Integer = 6

        Private Sub New(fuente As String)
            _fuente = fuente
        End Sub

        Private Shared Function CrearHttp() As HttpClient
            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol Or SecurityProtocolType.Tls12
            Dim h As New HttpClient() With {.Timeout = TimeSpan.FromSeconds(60)}
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)")
            Return h
        End Function

        Public ReadOnly Property Fuente As String Implements IDescargaHistorica.Fuente
            Get
                Return _fuente
            End Get
        End Property

        Public ReadOnly Property Temporalidades As String() Implements IDescargaHistorica.Temporalidades
            Get
                Return If(_fuente = FuentesVelas.YAHOO, {"1d"}, {"1h", "4h", "1d"})
            End Get
        End Property

        Public ReadOnly Property DesdePorDefecto As Date Implements IDescargaHistorica.DesdePorDefecto
            Get
                Select Case _fuente
                    Case FuentesVelas.BITSTAMP : Return New Date(2011, 8, 1)
                    Case FuentesVelas.BINANCE : Return New Date(2017, 8, 1)
                End Select
                Return New Date(2002, 1, 1)
            End Get
        End Property

        Public Function Soporta(simbolo As String) As Boolean Implements IDescargaHistorica.Soporta
            Select Case _fuente
                Case FuentesVelas.BITSTAMP : Return FuentesHttpFormato.ParBitstamp(simbolo) IsNot Nothing
                Case FuentesVelas.BINANCE : Return FuentesHttpFormato.ParBinance(simbolo) IsNot Nothing
            End Select
            Return FuentesHttpFormato.TickerYahoo(simbolo) IsNot Nothing
        End Function

        Public ReadOnly Property EstaDescargando As Boolean Implements IDescargaHistorica.EstaDescargando
            Get
                Return Volatile.Read(_activo) = 1
            End Get
        End Property

        Public Function Estado() As JObject Implements IDescargaHistorica.Estado
            SyncLock _bloqueo
                Return CType(_estado.DeepClone(), JObject)
            End SyncLock
        End Function

        Public Sub Detener() Implements IDescargaHistorica.Detener
            _cts?.Cancel()
        End Sub

        Private Sub Actualizar(accion As Action(Of JObject))
            SyncLock _bloqueo
                accion(_estado)
            End SyncLock
        End Sub

        Private Function Err(msg As String) As JObject
            Return New JObject From {{"ok", False}, {"error", msg}}
        End Function

        ''' <summary>Valida y lanza la descarga en segundo plano. Se sigue con Estado().</summary>
        Public Function Iniciar(simbolos As IList(Of String), temporalidades As IList(Of String),
                                desde As Date, hasta As Date) As JObject Implements IDescargaHistorica.Iniciar
            Dim tfs = If(temporalidades Is Nothing OrElse temporalidades.Count = 0, Me.Temporalidades.ToList(), temporalidades.ToList())
            Dim malos = tfs.Where(Function(t) Not Me.Temporalidades.Contains(t)).ToList()
            If malos.Count > 0 Then Return Err(_fuente & " no da " & String.Join(", ", malos) & " (solo " & String.Join(", ", Me.Temporalidades) & ").")
            If simbolos Is Nothing OrElse simbolos.Count = 0 Then Return Err("Indica al menos un simbolo.")
            Dim sinFuente = simbolos.Where(Function(s) Not Soporta(s)).ToList()
            If sinFuente.Count > 0 Then Return Err(_fuente & " no tiene: " & String.Join(", ", sinFuente) & ". " & FuentesVelas.Descripcion(_fuente))
            If desde > hasta Then Return Err("'desde' es posterior a 'hasta'.")
            If Interlocked.CompareExchange(_activo, 1, 0) = 1 Then Return Err("Ya hay una descarga de " & _fuente & " en curso.")
            _cts = New CancellationTokenSource()
            Dim ct = _cts.Token
            Tareas.EnSegundoPlano(Task.Run(Async Function()
                                                Try
                                                    Await DescargarAsync(simbolos.ToList(), tfs, desde, hasta, ct)
                                                Finally
                                                    Interlocked.Exchange(_activo, 0)
                                                End Try
                                            End Function), "Descarga " & _fuente)
            Return New JObject From {
                {"ok", True},
                {"mensaje", String.Format("Descarga {0} iniciada: {1} [{2}] {3:yyyy-MM-dd} -> {4:yyyy-MM-dd}. Sigue el avance en /api/heavy/historico-externo/estado?fuente={0}.",
                                          _fuente, String.Join(", ", simbolos), String.Join(", ", tfs), desde, hasta)}
            }
        End Function

        ''' <summary>Descarga sin lanzar en segundo plano (la usan Iniciar y la actualizacion nocturna).</summary>
        Public Async Function DescargarAsync(simbolos As List(Of String), tfs As List(Of String),
                                             desde As Date, hasta As Date, ct As CancellationToken) As Task(Of JObject)
            Dim hoy = DateTime.UtcNow.Date
            Dim hastaEx = If(hasta.Date.AddDays(1) < hoy, hasta.Date.AddDays(1), hoy)
            Dim porSerie As New JObject()
            Dim fallidos As New JArray()
            Actualizar(Sub(e)
                           e.RemoveAll()
                           e("activo") = True
                           e("fuente") = _fuente
                           e("inicio_utc") = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                           e("simbolos") = New JArray(simbolos)
                           e("temporalidades") = New JArray(tfs)
                           e("desde") = desde.ToString("yyyy-MM-dd")
                           e("hasta") = hastaEx.AddDays(-1).ToString("yyyy-MM-dd")
                           e("peticiones") = 0
                           e("meses_omitidos_ya_guardados") = 0
                           e("velas_guardadas") = 0
                           e("por_serie") = porSerie
                           e("archivos_fallidos") = fallidos
                           e("mensaje") = "Empezando..."
                       End Sub)
            Dim repo As New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString())
            Dim cancelado = False
            Try
                For Each sym In simbolos
                    If _fuente = FuentesVelas.YAHOO Then
                        Await DescargarDiarioYahooAsync(repo, sym, desde.Date, hastaEx, porSerie, fallidos, ct)
                    Else
                        Await DescargarHorasAsync(repo, sym, tfs, desde.Date, hastaEx, porSerie, fallidos, ct)
                    End If
                Next
            Catch ex As OperationCanceledException
                cancelado = True
            Catch ex As Exception
                AppLogger.Warn("[" & _fuente & "] Descarga: " & ex.Message)
                Actualizar(Sub(e) e("error") = ex.Message)
            End Try
            Actualizar(Sub(e)
                           e("activo") = False
                           e("fin_utc") = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                           Dim msg = If(cancelado, "Detenida.", If(e("error") IsNot Nothing, "Termino con error: " & e("error").ToString(), "Terminada."))
                           msg &= String.Format(" {0} velas guardadas.", e("velas_guardadas"))
                           Dim nf = CType(e("archivos_fallidos"), JArray).Count
                           If nf > 0 Then msg &= String.Format(" {0} peticion(es) no respondieron: vuelve a lanzar la descarga para completarlas; lo ya guardado no se repite.", nf)
                           e("mensaje") = msg
                       End Sub)
            AppLogger.Info("[" & _fuente & "] " & Estado()("mensaje").ToString())
            Return Estado()
        End Function

        Private Async Function DescargarHorasAsync(repo As CandleRepository, sym As String, tfs As List(Of String),
                                                   desde As Date, hastaEx As Date, porSerie As JObject, fallidos As JArray,
                                                   ct As CancellationToken) As Task
            Dim mesActual = New Date(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1)
            Dim guardados As HashSet(Of String) = Nothing
            For Each tf In tfs
                Dim s = Await repo.PeriodosConDatosAsync(_fuente, sym, tf, "YYYY-MM")
                If guardados Is Nothing Then guardados = s Else guardados.IntersectWith(s)
            Next
            ' Siempre meses completos (aunque 'desde' caiga a mitad de mes): asi un mes guardado esta entero
            For Each mes In DukascopyFormato.Meses(desde, hastaEx)
                ct.ThrowIfCancellationRequested()
                Dim clave = mes.ToString("yyyy-MM")
                If mes < mesActual.AddMonths(-1) AndAlso guardados.Contains(clave) Then
                    Actualizar(Sub(e) e("meses_omitidos_ya_guardados") = CInt(e("meses_omitidos_ya_guardados")) + 1)
                    Continue For
                End If
                Actualizar(Sub(e) e("mensaje") = String.Format("{0} {1} {2}", _fuente, sym, clave))
                Dim ini = DukascopyFormato.EpochDe(mes)
                Dim fin = DukascopyFormato.EpochDe(If(mes.AddMonths(1) < hastaEx, mes.AddMonths(1), hastaEx))
                Dim velasH = Await HorasAsync(sym, ini, fin, fallidos, ct)
                If velasH IsNot Nothing AndAlso velasH.Count > 0 Then
                    For Each tf In tfs
                        Dim lote = If(tf = "1h", velasH, DukascopyFormato.Agregar(velasH, DukascopyFormato.SegundosTf(tf)))
                        Await GuardarAsync(repo, sym, tf, lote, porSerie)
                    Next
                End If
                Await Task.Delay(PausaEntrePeticionesMs, ct)
            Next
        End Function

        ''' <summary>Velas de 1h de [ini, fin) (fin - ini cabe en una peticion: un mes).</summary>
        Private Async Function HorasAsync(sym As String, ini As Long, fin As Long, fallidos As JArray,
                                          ct As CancellationToken) As Task(Of List(Of CandleModel))
            If _fuente = FuentesVelas.BITSTAMP Then
                Dim json = Await BajarTextoAsync(FuentesHttpFormato.UrlBitstamp(FuentesHttpFormato.ParBitstamp(sym), ini, fin), fallidos, ct)
                Return If(json Is Nothing, Nothing, FuentesHttpFormato.LeerBitstamp(json, ini, fin))
            End If
            ' Binance: el espejo publico de datos primero; si no responde, la API principal
            For i = 0 To BasesBinance.Length - 1
                Dim ultimo = i = BasesBinance.Length - 1
                Dim json = Await BajarTextoAsync(FuentesHttpFormato.UrlBinance(BasesBinance(i), FuentesHttpFormato.ParBinance(sym), ini, fin),
                                                 If(ultimo, fallidos, Nothing), ct)
                If json IsNot Nothing Then Return FuentesHttpFormato.LeerBinance(json, ini, fin)
            Next
            Return Nothing
        End Function

        Private Async Function DescargarDiarioYahooAsync(repo As CandleRepository, sym As String, desde As Date, hastaEx As Date,
                                                         porSerie As JObject, fallidos As JArray, ct As CancellationToken) As Task
            Actualizar(Sub(e) e("mensaje") = String.Format("{0} {1} diario", _fuente, sym))
            Dim fin = DukascopyFormato.EpochDe(hastaEx)
            Dim json = Await BajarTextoAsync(FuentesHttpFormato.UrlYahoo(FuentesHttpFormato.TickerYahoo(sym), DukascopyFormato.EpochDe(desde), fin), fallidos, ct)
            If json Is Nothing Then Return
            Await GuardarAsync(repo, sym, "1d", FuentesHttpFormato.LeerYahoo(json, fin), porSerie)
        End Function

        Private Async Function GuardarAsync(repo As CandleRepository, sym As String, tf As String,
                                            lote As List(Of CandleModel), porSerie As JObject) As Task
            If lote.Count = 0 Then Return
            Dim n = Await repo.GuardarLoteAsync(_fuente, sym, tf, lote)
            Actualizar(Sub(e)
                           e("velas_guardadas") = CInt(e("velas_guardadas")) + n
                           Dim clave = sym & " " & tf
                           porSerie(clave) = If(porSerie(clave) Is Nothing, 0, CInt(porSerie(clave))) + n
                       End Sub)
        End Function

        ''' <summary>
        ''' Texto de la respuesta. 404 = no hay datos (Nothing, sin error). Red caida, 429 y 5xx
        ''' se reintentan; otro 4xx (simbolo invalido...) no. Si nunca responde, queda en
        ''' 'fallidos' (si se pasa) y devuelve Nothing.
        ''' </summary>
        Private Async Function BajarTextoAsync(url As String, fallidos As JArray, ct As CancellationToken) As Task(Of String)
            Dim ultimo = ""
            For intento = 1 To Math.Max(1, Reintentos)
                ct.ThrowIfCancellationRequested()
                Actualizar(Sub(e) e("peticiones") = CInt(e("peticiones")) + 1)
                Try
                    Using resp = Await Http.GetAsync(url, ct)
                        If resp.StatusCode = HttpStatusCode.NotFound Then Return Nothing
                        If resp.IsSuccessStatusCode Then Return Await resp.Content.ReadAsStringAsync()
                        ultimo = "HTTP " & CInt(resp.StatusCode).ToString()
                        Dim codigo = CInt(resp.StatusCode)
                        If codigo >= 400 AndAlso codigo < 500 AndAlso codigo <> 429 Then Exit For
                    End Using
                Catch ex As OperationCanceledException When ct.IsCancellationRequested
                    Throw
                Catch ex As Exception
                    ultimo = ex.GetBaseException().Message
                End Try
                If intento < Reintentos Then Await Task.Delay(DukascopyDescarga.EsperaReintento(intento), ct)
            Next
            If fallidos IsNot Nothing Then
                Actualizar(Sub(e)
                               If fallidos.Count < 200 Then fallidos.Add(url & " (" & ultimo & ")")
                           End Sub)
            End If
            AppLogger.Warn("[" & _fuente & "] Sin respuesta: " & url & " (" & ultimo & ")")
            Return Nothing
        End Function

        ''' <summary>
        ''' Actualizacion nocturna: cada serie ya guardada de esta fuente se completa desde su
        ''' ultima vela (menos un dia) hasta ayer. Si no hay ninguna, no hace nada.
        ''' </summary>
        Public Async Function ActualizarTodoAsync(Optional ct As CancellationToken = Nothing) As Task(Of JObject) Implements IDescargaHistorica.ActualizarTodoAsync
            Dim repo As New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString())
            Dim series = (Await repo.ListarSeriesAsync(_fuente)).Where(Function(s) Soporta(s.Item1) AndAlso Temporalidades.Contains(s.Item2)).ToList()
            If series.Count = 0 Then Return New JObject From {{"ok", True}, {"series", 0}, {"mensaje", "Sin series de " & _fuente & " guardadas."}}
            If Interlocked.CompareExchange(_activo, 1, 0) = 1 Then Return New JObject From {{"ok", False}, {"error", "Ya hay una descarga de " & _fuente & " en curso."}}
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
    End Class

End Namespace

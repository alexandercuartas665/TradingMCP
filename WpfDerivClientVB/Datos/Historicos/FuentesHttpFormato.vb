Imports System.Collections.Generic
Imports System.Globalization
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB

    ''' <summary>
    ''' URLs y lectura de las APIs publicas de Bitstamp, Binance y Yahoo (velas OHLC, hora UTC).
    ''' Funciones puras para poder probarlas. Las velas sin volumen (horas sin operaciones que
    ''' la fuente rellena con una vela plana) y las imposibles se descartan.
    ''' </summary>
    Public Module FuentesHttpFormato

        Private ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

        Private ReadOnly TickersYahoo As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"OTC_NDX", "^NDX"}, {"OTC_GDAXI", "^GDAXI"}, {"OTC_SPC", "^GSPC"}, {"OTC_DJI", "^DJI"},
            {"OTC_FTSE", "^FTSE"}, {"OTC_N225", "^N225"}, {"OTC_FCHI", "^FCHI"}, {"OTC_AS51", "^AXJO"}
        }

        ''' <summary>cryBTCUSD -> btcusd. Nothing si no es una cripto contra USD de Deriv.</summary>
        Public Function ParBitstamp(simbolo As String) As String
            Dim b = BaseCripto(simbolo)
            Return If(b Is Nothing, Nothing, b.ToLowerInvariant() & "usd")
        End Function

        ''' <summary>cryBTCUSD -> BTCUSDT (Binance cotiza contra USDT, no USD).</summary>
        Public Function ParBinance(simbolo As String) As String
            Dim b = BaseCripto(simbolo)
            Return If(b Is Nothing, Nothing, b.ToUpperInvariant() & "USDT")
        End Function

        ''' <summary>OTC_NDX -> ^NDX. Nothing si Yahoo no tiene ese indice.</summary>
        Public Function TickerYahoo(simbolo As String) As String
            If String.IsNullOrWhiteSpace(simbolo) Then Return Nothing
            Dim t As String = Nothing
            Return If(TickersYahoo.TryGetValue(simbolo.Trim(), t), t, Nothing)
        End Function

        Private Function BaseCripto(simbolo As String) As String
            If String.IsNullOrWhiteSpace(simbolo) Then Return Nothing
            Dim s = simbolo.Trim()
            If s.Length < 7 OrElse Not s.StartsWith("cry", StringComparison.OrdinalIgnoreCase) OrElse
               Not s.EndsWith("USD", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Dim b = s.Substring(3, s.Length - 6)
            For Each ch In b
                If Not Char.IsLetterOrDigit(ch) Then Return Nothing
            Next
            Return b
        End Function

        ''' <summary>
        ''' Velas de 1h de Bitstamp. Con start y end devuelve las 'limit' velas que terminan en
        ''' end (un mes son 744 horas, cabe en una peticion); hay que filtrar por el rango.
        ''' </summary>
        Public Function UrlBitstamp(par As String, desdeEpoch As Long, hastaEpochEx As Long) As String
            Return String.Format(Inv, "https://www.bitstamp.net/api/v2/ohlc/{0}/?step=3600&limit=1000&start={1}&end={2}",
                                 par, desdeEpoch, hastaEpochEx - 3600)
        End Function

        ''' <summary>Velas de 1h de Binance (base: data-api.binance.vision o api.binance.com).</summary>
        Public Function UrlBinance(baseUrl As String, par As String, desdeEpoch As Long, hastaEpochEx As Long) As String
            Return String.Format(Inv, "{0}/api/v3/klines?symbol={1}&interval=1h&startTime={2}&endTime={3}&limit=1000",
                                 baseUrl, par, desdeEpoch * 1000L, hastaEpochEx * 1000L - 1)
        End Function

        ''' <summary>Velas diarias de Yahoo entre dos instantes (segundos UTC).</summary>
        Public Function UrlYahoo(ticker As String, desdeEpoch As Long, hastaEpochEx As Long) As String
            Return String.Format(Inv, "https://query1.finance.yahoo.com/v8/finance/chart/{0}?period1={1}&period2={2}&interval=1d",
                                 Uri.EscapeDataString(ticker), desdeEpoch, hastaEpochEx)
        End Function

        ''' <summary>{"data": {"ohlc": [{"timestamp": "...", "open": "...", ..., "volume": "..."}]}}</summary>
        Public Function LeerBitstamp(json As String, desdeEpoch As Long, hastaEpochEx As Long) As List(Of CandleModel)
            Dim res As New List(Of CandleModel)()
            Dim filas = TryCast(JObject.Parse(json).SelectToken("data.ohlc"), JArray)
            If filas Is Nothing Then Return res
            For Each x In filas
                Dim e = Long.Parse(x.Value(Of String)("timestamp"), Inv)
                If e < desdeEpoch OrElse e >= hastaEpochEx Then Continue For
                If Dec(x("volume")) <= 0D Then Continue For
                Agregar(res, e, Dec(x("open")), Dec(x("high")), Dec(x("low")), Dec(x("close")))
            Next
            Return res
        End Function

        ''' <summary>[[openTime ms, "open", "high", "low", "close", "volume", closeTime, ...], ...]</summary>
        Public Function LeerBinance(json As String, desdeEpoch As Long, hastaEpochEx As Long) As List(Of CandleModel)
            Dim res As New List(Of CandleModel)()
            Dim filas = TryCast(JToken.Parse(json), JArray)
            If filas Is Nothing Then Return res
            For Each x In filas
                Dim e = CLng(x(0)) \ 1000L
                If e < desdeEpoch OrElse e >= hastaEpochEx Then Continue For
                If Dec(x(5)) <= 0D Then Continue For
                Agregar(res, e, Dec(x(1)), Dec(x(2)), Dec(x(3)), Dec(x(4)))
            Next
            Return res
        End Function

        ''' <summary>
        ''' {"chart": {"result": [{"timestamp": [...], "indicators": {"quote": [{"open": [...], ...}]}}]}}.
        ''' La marca es la apertura de la sesion local (Nueva York, Frankfurt...), que en UTC cae el
        ''' mismo dia: la vela se guarda a las 00:00 UTC de ese dia, como las diarias de Deriv.
        ''' Los dias con valores nulos (festivos) se omiten. Precios redondeados a 4 decimales.
        ''' </summary>
        Public Function LeerYahoo(json As String, hastaEpochEx As Long) As List(Of CandleModel)
            Dim res As New List(Of CandleModel)()
            Dim r = JObject.Parse(json).SelectToken("chart.result[0]")
            If r Is Nothing Then Return res
            Dim ts = TryCast(r("timestamp"), JArray)
            Dim q = r.SelectToken("indicators.quote[0]")
            If ts Is Nothing OrElse q Is Nothing Then Return res
            For i = 0 To ts.Count - 1
                Dim t = CLng(ts(i))
                Dim dia = t - (t Mod 86400L)
                If dia >= hastaEpochEx Then Continue For
                Dim o = q("open")?(i), h = q("high")?(i), l = q("low")?(i), c = q("close")?(i)
                If EsNulo(o) OrElse EsNulo(h) OrElse EsNulo(l) OrElse EsNulo(c) Then Continue For
                If res.Count > 0 AndAlso res(res.Count - 1).Epoch = dia Then Continue For
                Agregar(res, dia, R4(o), R4(h), R4(l), R4(c))
            Next
            Return res
        End Function

        Private Function EsNulo(t As JToken) As Boolean
            Return t Is Nothing OrElse t.Type = JTokenType.Null
        End Function

        Private Function Dec(t As JToken) As Decimal
            If EsNulo(t) Then Return 0D
            Return Decimal.Parse(t.ToString(), NumberStyles.Float, Inv)
        End Function

        Private Function R4(t As JToken) As Decimal
            Return Math.Round(CDec(t.Value(Of Double)()), 4)
        End Function

        Public Function Valida(o As Decimal, h As Decimal, l As Decimal, c As Decimal) As Boolean
            Return l > 0D AndAlso h >= l AndAlso o >= l AndAlso o <= h AndAlso c >= l AndAlso c <= h
        End Function

        Private Sub Agregar(res As List(Of CandleModel), e As Long, o As Decimal, h As Decimal, l As Decimal, c As Decimal)
            If Not Valida(o, h, l, c) Then Return
            res.Add(New CandleModel With {
                .Epoch = e,
                .OpenTime = DateTimeOffset.FromUnixTimeSeconds(e).UtcDateTime.ToString("yyyy-MM-dd HH:mm"),
                .OpenPrice = o, .High = h, .Low = l, .ClosePrice = c, .Status = "closed"
            })
        End Sub
    End Module

End Namespace

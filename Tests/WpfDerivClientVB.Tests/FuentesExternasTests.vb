Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports WpfDerivClientVB.WpfDerivClientVB

''' <summary>Fuentes externas por HTTP (Bitstamp, Binance, Yahoo): simbolos, URLs, lectura y catalogo.</summary>
<TestClass>
Public Class FuentesExternasTests

    Private Const ENE_2024 As Long = 1704067200   ' 2024-01-01 00:00 UTC
    Private Const FEB_2024 As Long = 1706745600   ' 2024-02-01 00:00 UTC

    Private Shared Function Vela(epoch As Long, o As Decimal, h As Decimal, l As Decimal, c As Decimal) As CandleModel
        Return New CandleModel With {.Epoch = epoch, .OpenPrice = o, .High = h, .Low = l, .ClosePrice = c}
    End Function

    <TestMethod>
    Public Sub Simbolos_de_Deriv_a_cada_fuente()
        Assert.AreEqual("btcusd", FuentesHttpFormato.ParBitstamp("cryBTCUSD"))
        Assert.AreEqual("ethusd", FuentesHttpFormato.ParBitstamp("cryETHUSD"))
        Assert.AreEqual("BTCUSDT", FuentesHttpFormato.ParBinance("cryBTCUSD"))
        Assert.IsNull(FuentesHttpFormato.ParBitstamp("frxEURUSD"))
        Assert.IsNull(FuentesHttpFormato.ParBinance("R_100"))
        Assert.IsNull(FuentesHttpFormato.ParBinance("cryUSD"))
        Assert.AreEqual("^NDX", FuentesHttpFormato.TickerYahoo("OTC_NDX"))
        Assert.AreEqual("^GDAXI", FuentesHttpFormato.TickerYahoo("otc_gdaxi"))
        Assert.IsNull(FuentesHttpFormato.TickerYahoo("cryBTCUSD"))
    End Sub

    <TestMethod>
    Public Sub Catalogo_de_fuentes_y_descargadores()
        CollectionAssert.AreEqual({"Deriv", "Dukascopy", "Bitstamp", "Binance", "Yahoo"}, FuentesVelas.Todas)
        Assert.IsFalse(FuentesVelas.EsExterna("deriv"))
        Assert.IsTrue(FuentesVelas.EsExterna("yahoo"))
        Assert.IsNull(DescargasHistoricas.Para("Deriv"))
        Assert.AreEqual(4, DescargasHistoricas.Todas().Count)
        Dim y = DescargasHistoricas.Para("yahoo")
        Assert.IsTrue(y.Soporta("OTC_GDAXI"))
        Assert.IsFalse(y.Soporta("cryBTCUSD"))
        CollectionAssert.AreEqual({"1d"}, y.Temporalidades)
        Assert.AreEqual(New Date(2002, 1, 1), y.DesdePorDefecto)
        Assert.IsTrue(DescargasHistoricas.Para("Bitstamp").Soporta("cryBTCUSD"))
        Assert.IsFalse(DescargasHistoricas.Para("Bitstamp").Soporta("OTC_NDX"))
        Assert.IsTrue(DescargasHistoricas.Para("Dukascopy").Soporta("OTC_GDAXI"))
    End Sub

    <TestMethod>
    Public Sub Iniciar_rechaza_temporalidades_y_simbolos_que_la_fuente_no_tiene()
        ' Validaciones antes de lanzar: no se baja nada
        Dim r = DescargaHttpExterna.Yahoo.Iniciar({"OTC_NDX"}, {"1h"}, New Date(2020, 1, 1), New Date(2020, 2, 1))
        Assert.IsFalse(CBool(r("ok")))
        StringAssert.Contains(r("error").ToString(), "1h")
        r = DescargaHttpExterna.Bitstamp.Iniciar({"OTC_NDX"}, Nothing, New Date(2020, 1, 1), New Date(2020, 2, 1))
        Assert.IsFalse(CBool(r("ok")))
        StringAssert.Contains(r("error").ToString(), "OTC_NDX")
        r = DescargaHttpExterna.Binance.Iniciar({"cryBTCUSD"}, Nothing, New Date(2020, 2, 1), New Date(2020, 1, 1))
        Assert.IsFalse(CBool(r("ok")))
        Assert.IsFalse(DescargaHttpExterna.Binance.EstaDescargando)
    End Sub

    <TestMethod>
    Public Sub Urls_de_un_mes()
        Dim b = FuentesHttpFormato.UrlBitstamp("btcusd", ENE_2024, FEB_2024)
        StringAssert.Contains(b, "/ohlc/btcusd/")
        StringAssert.Contains(b, "step=3600")
        StringAssert.Contains(b, "start=1704067200&end=1706742000")
        Dim n = FuentesHttpFormato.UrlBinance("https://data-api.binance.vision", "BTCUSDT", ENE_2024, FEB_2024)
        Assert.AreEqual("https://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT&interval=1h&startTime=1704067200000&endTime=1706745599999&limit=1000", n)
        Dim y = FuentesHttpFormato.UrlYahoo("^NDX", ENE_2024, FEB_2024)
        StringAssert.Contains(y, "/chart/%5ENDX?period1=1704067200&period2=1706745600&interval=1d")
    End Sub

    <TestMethod>
    Public Sub Lee_Bitstamp_filtrando_rango_sin_volumen_e_imposibles()
        Dim json = "{""data"": {""pair"": ""BTC/USD"", ""ohlc"": [" &
                   "{""timestamp"": ""1704063600"", ""open"": ""1"", ""high"": ""2"", ""low"": ""1"", ""close"": ""2"", ""volume"": ""5""}," &
                   "{""timestamp"": ""1704067200"", ""open"": ""42283.58"", ""high"": ""42554.57"", ""low"": ""42261.02"", ""close"": ""42475.23"", ""volume"": ""12.5""}," &
                   "{""timestamp"": ""1704070800"", ""open"": ""42475"", ""high"": ""42475"", ""low"": ""42475"", ""close"": ""42475"", ""volume"": ""0.00000000""}," &
                   "{""timestamp"": ""1704074400"", ""open"": ""10"", ""high"": ""9"", ""low"": ""11"", ""close"": ""10"", ""volume"": ""1""}" &
                   "]}}"
        Dim v = FuentesHttpFormato.LeerBitstamp(json, ENE_2024, FEB_2024)
        Assert.AreEqual(1, v.Count)
        Assert.AreEqual(ENE_2024, v(0).Epoch)
        Assert.AreEqual(42283.58D, v(0).OpenPrice)
        Assert.AreEqual(42554.57D, v(0).High)
        Assert.AreEqual(42261.02D, v(0).Low)
        Assert.AreEqual(42475.23D, v(0).ClosePrice)
        Assert.AreEqual("2024-01-01 00:00", v(0).OpenTime)
    End Sub

    <TestMethod>
    Public Sub Lee_Binance_en_milisegundos()
        Dim json = "[[1704067200000,""42283.58000000"",""42554.57000000"",""42261.02000000"",""42475.23000000"",""1271.68108000"",1704070799999,""0"",1,""0"",""0"",""0""]," &
                   "[1704070800000,""42475.23"",""42475.23"",""42475.23"",""42475.23"",""0.0"",1704074399999,""0"",0,""0"",""0"",""0""]," &
                   "[1706745600000,""1"",""2"",""1"",""2"",""3"",1706749199999,""0"",1,""0"",""0"",""0""]]"
        Dim v = FuentesHttpFormato.LeerBinance(json, ENE_2024, FEB_2024)
        Assert.AreEqual(1, v.Count)
        Assert.AreEqual(ENE_2024, v(0).Epoch)
        Assert.AreEqual(42475.23D, v(0).ClosePrice)
    End Sub

    <TestMethod>
    Public Sub Lee_Yahoo_a_las_00_UTC_sin_festivos_ni_el_dia_en_curso()
        ' 2024-01-02 y 2024-01-03 abren 14:30 UTC; el 2024-01-04 viene nulo (festivo); 2024-01-05 es "hoy" (excluido)
        Dim json = "{""chart"":{""result"":[{""meta"":{""symbol"":""^NDX""}," &
                   """timestamp"":[1704205800,1704292200,1704378600,1704465000]," &
                   """indicators"":{""quote"":[{" &
                   """open"":[16543.94140625,16320.5,null,16200]," &
                   """high"":[16550.123456,16400.2,null,16300]," &
                   """low"":[16300.5,16200.1,null,16100]," &
                   """close"":[16390.8,16368.49,null,16250]," &
                   """volume"":[0,0,null,0]}]}}],""error"":null}}"
        Dim hoy = 1704412800L   ' 2024-01-05 00:00 UTC
        Dim v = FuentesHttpFormato.LeerYahoo(json, hoy)
        Assert.AreEqual(2, v.Count)
        Assert.AreEqual(1704153600L, v(0).Epoch)   ' 2024-01-02 00:00 UTC
        Assert.AreEqual(1704240000L, v(1).Epoch)
        Assert.AreEqual(16543.9414D, v(0).OpenPrice)
        Assert.AreEqual(16550.1235D, v(0).High)
    End Sub

    <TestMethod>
    Public Sub Respuesta_vacia_o_sin_resultado_no_da_velas()
        Assert.AreEqual(0, FuentesHttpFormato.LeerBitstamp("{""data"": {""ohlc"": []}}", 0, Long.MaxValue).Count)
        Assert.AreEqual(0, FuentesHttpFormato.LeerBinance("[]", 0, Long.MaxValue).Count)
        Assert.AreEqual(0, FuentesHttpFormato.LeerYahoo("{""chart"":{""result"":null,""error"":{""code"":""Not Found""}}}", Long.MaxValue).Count)
    End Sub

    <TestMethod>
    Public Sub Comparar_con_otra_fuente_nombra_la_fuente()
        Dim deriv As New List(Of CandleModel) From {Vela(86400, 1D, 2D, 0.5D, 1.5D)}
        Dim ext As New List(Of CandleModel) From {Vela(86400, 1D, 2D, 0.5D, 1.6D)}
        Dim r = DukascopyDescarga.Comparar(deriv, ext, "OTC_NDX", "1d", FuentesVelas.YAHOO)
        Assert.AreEqual("Yahoo", r("fuente").ToString())
        Assert.AreEqual(1, CInt(r("velas_yahoo")))
        Assert.AreEqual(100.0, CDbl(r("misma_direccion_pct")), 0.001)
        ' Sin fuente sigue siendo Dukascopy (compatibilidad de la API)
        Dim r2 = DukascopyDescarga.Comparar(deriv, ext, "OTC_NDX", "1d")
        Assert.AreEqual(1, CInt(r2("velas_dukascopy")))
    End Sub

    <TestMethod>
    Public Sub Texto_de_cobertura_avisa_las_combinaciones_sin_velas()
        Dim mapa As New Dictionary(Of String, Tuple(Of Long, Long, Long)) From {
            {"cryBTCUSD|1h", Tuple.Create(127249L, 1313625600L, 1795392000L)}
        }
        Dim t = Global.WpfDerivClientVB.HeavyStrategyTesterWindow.TextoCobertura(
            "Bitstamp", mapa, {"cryBTCUSD", "OTC_NDX"}, {"1h"})
        StringAssert.Contains(t, "Bitstamp:")
        StringAssert.Contains(t, "cryBTCUSD 1h: 127")
        StringAssert.Contains(t, "2011-08-18 a ")
        StringAssert.Contains(t, "OTC_NDX 1h: sin velas en Bitstamp")
        StringAssert.Contains(t, "AVISO: 1 combinacion(es)")
    End Sub
End Class

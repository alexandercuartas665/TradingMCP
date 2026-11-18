Imports System.IO
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports WpfDerivClientVB.WpfDerivClientVB

''' <summary>Historico externo de Dukascopy: formato .bi5, agregacion, simbolos y comparacion con Deriv.</summary>
<TestClass>
Public Class HistoricoExternoTests

    ''' <summary>Registro de 24 bytes big-endian como los de Dukascopy.</summary>
    Private Shared Sub Registro(ms As MemoryStream, seg As Integer, o As Integer, c As Integer, l As Integer, h As Integer, vol As Single)
        For Each v In {seg, o, c, l, h}
            Dim b = BitConverter.GetBytes(v)
            Array.Reverse(b)
            ms.Write(b, 0, 4)
        Next
        Dim f = BitConverter.GetBytes(vol)
        Array.Reverse(f)
        ms.Write(f, 0, 4)
    End Sub

    ''' <summary>Comprime en formato LZMA "alone" (el de los .bi5).</summary>
    Private Shared Function Comprimir(raw As Byte()) As Byte()
        Dim enc As New SevenZip.Compression.LZMA.Encoder()
        Using entrada As New MemoryStream(raw), salida As New MemoryStream()
            enc.WriteCoderProperties(salida)
            salida.Write(BitConverter.GetBytes(CLng(raw.Length)), 0, 8)
            enc.Code(entrada, salida, -1, -1, Nothing)
            Return salida.ToArray()
        End Using
    End Function

    Private Shared Function Vela(epoch As Long, o As Decimal, h As Decimal, l As Decimal, c As Decimal) As CandleModel
        Return New CandleModel With {.Epoch = epoch, .OpenPrice = o, .High = h, .Low = l, .ClosePrice = c}
    End Function

    <TestMethod>
    Public Sub Simbolos_de_Deriv_a_Dukascopy()
        Assert.AreEqual("EURUSD", DukascopyFormato.SimboloDukascopy("frxEURUSD"))
        Assert.AreEqual("XAUUSD", DukascopyFormato.SimboloDukascopy("frxXAUUSD"))
        Assert.AreEqual("USA500IDXUSD", DukascopyFormato.SimboloDukascopy("OTC_SPC"))
        Assert.AreEqual("USATECHIDXUSD", DukascopyFormato.SimboloDukascopy("OTC_NDX"))
        ' Los sinteticos de Deriv no existen fuera de Deriv
        Assert.IsNull(DukascopyFormato.SimboloDukascopy("R_100"))
        Assert.IsNull(DukascopyFormato.SimboloDukascopy(""))
    End Sub

    <TestMethod>
    Public Sub Divisor_segun_instrumento()
        Assert.AreEqual(100000D, DukascopyFormato.Divisor("EURUSD"))
        Assert.AreEqual(1000D, DukascopyFormato.Divisor("USDJPY"))
        Assert.AreEqual(1000D, DukascopyFormato.Divisor("XAUUSD"))
        Assert.AreEqual(1000D, DukascopyFormato.Divisor("USA500IDXUSD"))
    End Sub

    <TestMethod>
    Public Sub Urls_con_mes_desde_cero()
        Assert.AreEqual("https://datafeed.dukascopy.com/datafeed/EURUSD/2020/00/BID_candles_hour_1.bi5",
                        DukascopyFormato.UrlHoras("EURUSD", 2020, 1))
        Assert.AreEqual("https://datafeed.dukascopy.com/datafeed/EURUSD/2026/10/13/BID_candles_min_1.bi5",
                        DukascopyFormato.UrlMinutos("EURUSD", New Date(2026, 11, 13)))
    End Sub

    <TestMethod>
    Public Sub Lee_bi5_y_omite_velas_sin_volumen()
        Dim ms As New MemoryStream()
        Registro(ms, 0, 112076, 112100, 112050, 112120, 10.5F)
        Registro(ms, 3600, 112100, 112100, 112100, 112100, 0F)     ' relleno de fin de semana
        Registro(ms, 7200, 112100, 112000, 111990, 112130, 3.2F)
        Dim bi5 = Comprimir(ms.ToArray())
        Dim inicio = DukascopyFormato.EpochDe(New Date(2020, 1, 1))
        Dim velas = DukascopyFormato.LeerVelas(DukascopyFormato.Descomprimir(bi5), inicio, 100000D)
        Assert.AreEqual(2, velas.Count)
        Assert.AreEqual(inicio, velas(0).Epoch)
        Assert.AreEqual(1.12076D, velas(0).OpenPrice)
        Assert.AreEqual(1.121D, velas(0).ClosePrice)
        Assert.AreEqual(1.1205D, velas(0).Low)
        Assert.AreEqual(1.1212D, velas(0).High)
        Assert.AreEqual(inicio + 7200, velas(1).Epoch)
        Assert.AreEqual(1.12D, velas(1).ClosePrice)
    End Sub

    <TestMethod>
    Public Sub Pagina_de_error_no_se_descomprime_y_registros_imposibles_se_descartan()
        Dim html = Text.Encoding.ASCII.GetBytes("<html><body><h1>503 Service Unavailable</h1></body></html>")
        Assert.ThrowsException(Of InvalidDataException)(Function() DukascopyFormato.Descomprimir(html))
        Dim ms As New MemoryStream()
        Registro(ms, 0, 112076, 112100, 112150, 112120, 1.0F)     ' minimo por encima del maximo
        Registro(ms, 60, 0, 0, 0, 0, 1.0F)                        ' precios a cero
        Registro(ms, 120, 112076, 112100, 112050, 112120, 1.0F)   ' valida
        Dim velas = DukascopyFormato.LeerVelas(ms.ToArray(), 0, 100000D)
        Assert.AreEqual(1, velas.Count)
        Assert.AreEqual(120L, velas(0).Epoch)
        Assert.AreEqual("http://datafeed.dukascopy.com/datafeed/X.bi5", DukascopyDescarga.ConHttp("https://datafeed.dukascopy.com/datafeed/X.bi5"))
    End Sub

    <TestMethod>
    Public Sub Archivo_vacio_no_da_velas()
        Assert.AreEqual(0, DukascopyFormato.Descomprimir(New Byte() {}).Length)
        Assert.AreEqual(0, DukascopyFormato.LeerVelas(New Byte() {}, 0, 100000D).Count)
    End Sub

    <TestMethod>
    Public Sub Agrega_1h_en_4h_alineadas_a_UTC()
        Dim d0 = DukascopyFormato.EpochDe(New Date(2024, 3, 5))
        Dim horas As New List(Of CandleModel)
        For h = 0 To 7
            horas.Add(Vela(d0 + h * 3600, 1D + h, 1.5D + h, 0.5D + h, 1.2D + h))
        Next
        Dim h4 = DukascopyFormato.Agregar(horas, 14400)
        Assert.AreEqual(2, h4.Count)
        Assert.AreEqual(d0, h4(0).Epoch)
        Assert.AreEqual(1D, h4(0).OpenPrice)
        Assert.AreEqual(4.5D, h4(0).High)
        Assert.AreEqual(0.5D, h4(0).Low)
        Assert.AreEqual(4.2D, h4(0).ClosePrice)
        Assert.AreEqual(d0 + 14400, h4(1).Epoch)
        Assert.AreEqual(5D, h4(1).OpenPrice)
        ' Un hueco (sin la hora 01) no rompe el bloque
        horas.RemoveAt(1)
        Assert.AreEqual(2, DukascopyFormato.Agregar(horas, 14400).Count)
        Dim dia = DukascopyFormato.Agregar(horas, 86400)
        Assert.AreEqual(1, dia.Count)
        Assert.AreEqual(d0, dia(0).Epoch)
    End Sub

    <TestMethod>
    Public Sub Dias_sin_sabados_y_meses_del_rango()
        ' 2026-11-13 viernes .. 2026-11-16 lunes (exclusivo): viernes y domingo
        Dim dias = DukascopyFormato.Dias(New Date(2026, 11, 13), New Date(2026, 11, 16))
        CollectionAssert.AreEqual({New Date(2026, 11, 13), New Date(2026, 11, 15)}, dias)
        Dim meses = DukascopyFormato.Meses(New Date(2025, 11, 20), New Date(2026, 2, 1))
        CollectionAssert.AreEqual({New Date(2025, 11, 1), New Date(2025, 12, 1), New Date(2026, 1, 1)}, meses)
    End Sub

    <TestMethod>
    Public Sub Temporalidades_de_minutos_y_de_horas()
        Assert.IsTrue(DukascopyFormato.UsaMinutos("5m"))
        Assert.IsTrue(DukascopyFormato.UsaMinutos("1m"))
        Assert.IsFalse(DukascopyFormato.UsaMinutos("1h"))
        Assert.IsFalse(DukascopyFormato.UsaMinutos("1d"))
        Assert.AreEqual(0, DukascopyFormato.SegundosTf("2h"))
    End Sub

    <TestMethod>
    Public Sub Fuente_de_velas_normalizada()
        Assert.AreEqual(FuentesVelas.DERIV, FuentesVelas.Normalizar(Nothing))
        Assert.AreEqual(FuentesVelas.DERIV, FuentesVelas.Normalizar("deriv"))
        Assert.AreEqual(FuentesVelas.DUKASCOPY, FuentesVelas.Normalizar(" DUKASCOPY "))
        Assert.AreEqual(FuentesVelas.YAHOO, FuentesVelas.Normalizar("yahoo"))
        Assert.AreEqual(FuentesVelas.BITSTAMP, FuentesVelas.Normalizar("BitStamp"))
        Assert.ThrowsException(Of ArgumentException)(Function() FuentesVelas.Normalizar("kraken"))
    End Sub

    <TestMethod>
    Public Sub Esperas_de_reintento_crecen_hasta_30s()
        Assert.AreEqual(2000, DukascopyDescarga.EsperaReintento(1))
        Assert.AreEqual(4000, DukascopyDescarga.EsperaReintento(2))
        Assert.AreEqual(16000, DukascopyDescarga.EsperaReintento(4))
        Assert.AreEqual(30000, DukascopyDescarga.EsperaReintento(8))
    End Sub

    <TestMethod>
    Public Sub Compara_Deriv_con_Dukascopy()
        Dim deriv As New List(Of CandleModel) From {
            Vela(3600, 1D, 1.1D, 0.9D, 1.05D),      ' sube
            Vela(7200, 1.05D, 1.1D, 0.9D, 1D),      ' baja
            Vela(10800, 1D, 1.1D, 0.9D, 1.02D),     ' sube
            Vela(14400, 1D, 1.1D, 0.9D, 1.1D)       ' sin pareja en Dukascopy
        }
        Dim duka As New List(Of CandleModel) From {
            Vela(3600, 1D, 1.1D, 0.9D, 1.05D),      ' sube (igual)
            Vela(7200, 1.05D, 1.1D, 0.9D, 1.0D),    ' baja (igual)
            Vela(10800, 1.03D, 1.1D, 0.9D, 1.02D)   ' baja: distinta direccion, cierre igual
        }
        Dim r = DukascopyDescarga.Comparar(deriv, duka, "frxEURUSD", "1h")
        Assert.AreEqual(3, CInt(r("velas_comparadas")))
        Assert.AreEqual(66.67, CDbl(r("misma_direccion_pct")), 0.01)
        Assert.AreEqual(0.0, CDbl(r("diferencia_media_cierre_pct")), 0.0001)
    End Sub
End Class

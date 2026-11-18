Imports System.Collections.Generic
Imports System.IO
Imports System.Linq

Namespace WpfDerivClientVB

    ''' <summary>
    ''' Formato de los archivos historicos publicos de Dukascopy (datafeed.dukascopy.com).
    ''' Cada archivo .bi5 es LZMA ("alone"); dentro, registros de 24 bytes big-endian:
    ''' segundos desde el inicio del archivo, apertura, cierre, minimo, maximo (enteros en
    ''' puntos) y volumen (float). Precios BID, hora UTC. Funciones puras para poder probarlas.
    ''' </summary>
    Public Module DukascopyFormato

        Public Const URL_BASE As String = "https://datafeed.dukascopy.com/datafeed/"
        Private ReadOnly IndicesDeriv As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"OTC_DJI", "USA30IDXUSD"}, {"OTC_NDX", "USATECHIDXUSD"}, {"OTC_SPC", "USA500IDXUSD"},
            {"OTC_FTSE", "GBRIDXGBP"}, {"OTC_GDAXI", "DEUIDXEUR"}, {"OTC_N225", "JPNIDXJPY"},
            {"OTC_AS51", "AUSIDXAUD"}, {"OTC_FCHI", "FRAIDXEUR"}
        }

        ''' <summary>frxEURUSD -> EURUSD, OTC_SPC -> USA500IDXUSD. Nothing si Dukascopy no lo tiene (p. ej. R_100).</summary>
        Public Function SimboloDukascopy(simboloDeriv As String) As String
            If String.IsNullOrWhiteSpace(simboloDeriv) Then Return Nothing
            Dim s = simboloDeriv.Trim()
            If s.StartsWith("frx", StringComparison.OrdinalIgnoreCase) AndAlso s.Length = 9 Then Return s.Substring(3).ToUpperInvariant()
            Dim idx As String = Nothing
            If IndicesDeriv.TryGetValue(s, idx) Then Return idx
            Return Nothing
        End Function

        ''' <summary>Divisor de los enteros del archivo: 1000 para pares con JPY, metales e indices; 100000 para el resto.</summary>
        Public Function Divisor(simboloDuka As String) As Decimal
            Dim s = simboloDuka.ToUpperInvariant()
            If s.EndsWith("JPY") OrElse s.StartsWith("XAU") OrElse s.StartsWith("XAG") OrElse s.Contains("IDX") Then Return 1000D
            Return 100000D
        End Function

        ''' <summary>Velas de 1 hora de un mes completo. El mes de la URL empieza en 0 (enero = 00).</summary>
        Public Function UrlHoras(simboloDuka As String, anio As Integer, mes As Integer) As String
            Return String.Format("{0}{1}/{2:0000}/{3:00}/BID_candles_hour_1.bi5", URL_BASE, simboloDuka, anio, mes - 1)
        End Function

        ''' <summary>Velas de 1 minuto de un dia.</summary>
        Public Function UrlMinutos(simboloDuka As String, dia As Date) As String
            Return String.Format("{0}{1}/{2:0000}/{3:00}/{4:00}/BID_candles_min_1.bi5", URL_BASE, simboloDuka, dia.Year, dia.Month - 1, dia.Day)
        End Function

        ''' <summary>Descomprime un .bi5 (LZMA alone: 5 bytes de propiedades + 8 de tamano + datos).</summary>
        Public Function Descomprimir(bi5 As Byte()) As Byte()
            If bi5 Is Nothing OrElse bi5.Length < 13 Then Return New Byte() {}
            Using entrada As New MemoryStream(bi5), salida As New MemoryStream()
                Dim props(4) As Byte
                entrada.Read(props, 0, 5)
                Dim tam(7) As Byte
                entrada.Read(tam, 0, 8)
                Dim tamano = BitConverter.ToInt64(tam, 0)
                ' Cabecera imposible (p. ej. una pagina de error HTML con codigo 200): no intentar
                ' descomprimir, que podria reservar gigas de memoria
                Dim diccionario = BitConverter.ToUInt32(props, 1)
                If props(0) > 224 OrElse diccionario > 64UI * 1024UI * 1024UI OrElse tamano < -1 OrElse tamano > 50L * 1024L * 1024L Then
                    Throw New InvalidDataException("El archivo descargado no es un .bi5 valido.")
                End If
                Dim dec As New SevenZip.Compression.LZMA.Decoder()
                dec.SetDecoderProperties(props)
                dec.Code(entrada, salida, entrada.Length - entrada.Position, tamano, Nothing)
                Return salida.ToArray()
            End Using
        End Function

        Private Function Entero(d As Byte(), i As Integer) As Integer
            Return (CInt(d(i)) << 24) Or (CInt(d(i + 1)) << 16) Or (CInt(d(i + 2)) << 8) Or CInt(d(i + 3))
        End Function

        Private Function Flotante(d As Byte(), i As Integer) As Single
            Dim b = New Byte() {d(i + 3), d(i + 2), d(i + 1), d(i)}
            Return BitConverter.ToSingle(b, 0)
        End Function

        ''' <summary>
        ''' Lee los registros ya descomprimidos. Omite las velas sin volumen: Dukascopy rellena
        ''' fines de semana y festivos con velas planas que Deriv no tiene.
        ''' </summary>
        Public Function LeerVelas(datos As Byte(), epochInicioArchivo As Long, divisor As Decimal) As List(Of CandleModel)
            Dim res As New List(Of CandleModel)()
            If datos Is Nothing Then Return res
            Dim n = datos.Length \ 24
            For k = 0 To n - 1
                Dim i = k * 24
                If Flotante(datos, i + 20) <= 0.0F Then Continue For
                Dim o = Entero(datos, i + 4), c = Entero(datos, i + 8), l = Entero(datos, i + 12), h = Entero(datos, i + 16)
                ' Registro imposible (archivo danado): se descarta
                If l <= 0 OrElse h < l OrElse o < l OrElse o > h OrElse c < l OrElse c > h Then Continue For
                Dim epoch = epochInicioArchivo + Entero(datos, i)
                res.Add(New CandleModel With {
                    .Epoch = epoch,
                    .OpenTime = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime.ToString("yyyy-MM-dd HH:mm"),
                    .OpenPrice = o / divisor,
                    .ClosePrice = c / divisor,
                    .Low = l / divisor,
                    .High = h / divisor,
                    .Status = "closed"
                })
            Next
            Return res
        End Function

        ''' <summary>
        ''' Agrupa velas ordenadas en velas mayores alineadas al epoch (como Deriv: 4h empieza a
        ''' las 00, 04, 08... UTC y 1d a las 00 UTC). La vela agrupada abre a la hora del bloque.
        ''' </summary>
        Public Function Agregar(velas As IEnumerable(Of CandleModel), segundos As Integer) As List(Of CandleModel)
            Dim res As New List(Of CandleModel)()
            Dim actual As CandleModel = Nothing
            For Each v In velas.OrderBy(Function(x) x.Epoch)
                Dim bloque = v.Epoch - (v.Epoch Mod segundos)
                If actual Is Nothing OrElse actual.Epoch <> bloque Then
                    actual = New CandleModel With {
                        .Epoch = bloque,
                        .OpenTime = DateTimeOffset.FromUnixTimeSeconds(bloque).UtcDateTime.ToString("yyyy-MM-dd HH:mm"),
                        .OpenPrice = v.OpenPrice, .High = v.High, .Low = v.Low, .ClosePrice = v.ClosePrice, .Status = "closed"
                    }
                    res.Add(actual)
                Else
                    If v.High > actual.High Then actual.High = v.High
                    If v.Low < actual.Low Then actual.Low = v.Low
                    actual.ClosePrice = v.ClosePrice
                End If
            Next
            Return res
        End Function

        ''' <summary>Segundos de cada temporalidad que se puede construir con estos archivos.</summary>
        Public Function SegundosTf(tf As String) As Integer
            Select Case tf
                Case "1m" : Return 60
                Case "5m" : Return 300
                Case "15m" : Return 900
                Case "30m" : Return 1800
                Case "1h" : Return 3600
                Case "4h" : Return 14400
                Case "1d" : Return 86400
            End Select
            Return 0
        End Function

        ''' <summary>Las temporalidades menores de 1h salen de los archivos de minutos; el resto, de los de horas.</summary>
        Public Function UsaMinutos(tf As String) As Boolean
            Dim s = SegundosTf(tf)
            Return s > 0 AndAlso s < 3600
        End Function

        ''' <summary>Meses (primer dia) que cubren [desde, hastaExclusivo).</summary>
        Public Function Meses(desde As Date, hastaExclusivo As Date) As List(Of Date)
            Dim res As New List(Of Date)()
            Dim m = New Date(desde.Year, desde.Month, 1)
            While m < hastaExclusivo
                res.Add(m)
                m = m.AddMonths(1)
            End While
            Return res
        End Function

        ''' <summary>Dias habiles (sin sabados: los mercados que cubre Dukascopy cierran todo el sabado UTC).</summary>
        Public Function Dias(desde As Date, hastaExclusivo As Date) As List(Of Date)
            Dim res As New List(Of Date)()
            Dim d = desde.Date
            While d < hastaExclusivo
                If d.DayOfWeek <> DayOfWeek.Saturday Then res.Add(d)
                d = d.AddDays(1)
            End While
            Return res
        End Function

        Public Function EpochDe(d As Date) As Long
            Return New DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds()
        End Function
    End Module
End Namespace

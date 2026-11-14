' ============================================================
' PayoutsReales.vb
'
' Guarda el payout real medido con cotizaciones de Deriv
' (POST /api/heavy/payouts) para que el backtest lo use en vez
' de un valor supuesto (85%). Clave: simbolo + duracion del
' contrato (p. ej. "R_100|120m"). Si no hay medicion para esa
' duracion exacta se usa la media de las del simbolo.
'
' Archivo: %LOCALAPPDATA%\WpfDerivClientVB\payouts_reales.json
' ============================================================
Imports System.IO
Imports System.Linq
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas

Namespace WpfDerivClientVB.Backtesting

    Public Module PayoutsReales

        Private ReadOnly _lock As New Object()

        ''' <summary>Ruta del archivo (reemplazable en pruebas).</summary>
        Friend Property RutaOverride As String = Nothing

        Private Function Ruta() As String
            If Not String.IsNullOrEmpty(RutaOverride) Then Return RutaOverride
            Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "WpfDerivClientVB", "payouts_reales.json")
        End Function

        Private Function Leer() As JObject
            Try
                If File.Exists(Ruta()) Then Return JObject.Parse(File.ReadAllText(Ruta()))
            Catch ex As Exception
                AppLogger.Warn("[PayoutsReales] No se pudo leer: " & ex.Message)
            End Try
            Return New JObject()
        End Function

        ''' <summary>
        ''' Registra las filas medidas por /api/heavy/payouts (las que traen payout_pct).
        ''' Una medicion nueva reemplaza a la anterior de la misma clave.
        ''' </summary>
        Public Sub Registrar(mercados As JArray, medidoUtc As String)
            If mercados Is Nothing Then Return
            SyncLock _lock
                Dim datos = Leer()
                For Each m In mercados
                    If m("payout_pct") Is Nothing OrElse m("duracion") Is Nothing Then Continue For
                    datos(Clave(m("symbol").ToString(), m("duracion").ToString())) = New JObject From {
                        {"payout_pct", m("payout_pct")}, {"medido_utc", medidoUtc}
                    }
                Next
                Directory.CreateDirectory(Path.GetDirectoryName(Ruta()))
                File.WriteAllText(Ruta(), datos.ToString())
            End SyncLock
        End Sub

        ''' <summary>Payout real (%) para el simbolo y la duracion del contrato, o Nothing si no se ha medido.</summary>
        Public Function Obtener(symbol As String, timeframe As String, velas As Integer) As Decimal?
            SyncLock _lock
                Return ObtenerDe(Leer(), symbol, timeframe, velas)
            End SyncLock
        End Function

        Friend Function ObtenerDe(datos As JObject, symbol As String, timeframe As String, velas As Integer) As Decimal?
            If datos Is Nothing OrElse String.IsNullOrEmpty(symbol) Then Return Nothing
            Dim unidad As String = ""
            Dim dur = LiveTestMotor.DuracionDeriv(timeframe, velas, unidad)
            If dur > 0 Then
                Dim exacta = datos(Clave(symbol, dur.ToString() & unidad))
                If exacta IsNot Nothing Then Return CDec(exacta("payout_pct"))
            End If
            Dim prefijo = symbol.ToUpperInvariant() & "|"
            Dim del = datos.Properties().Where(Function(p) p.Name.StartsWith(prefijo)).Select(Function(p) CDec(p.Value("payout_pct"))).ToList()
            If del.Count = 0 Then Return Nothing
            Return Math.Round(del.Average(), 2)
        End Function

        Private Function Clave(symbol As String, duracion As String) As String
            Return symbol.ToUpperInvariant() & "|" & duracion.ToLowerInvariant()
        End Function

    End Module

End Namespace

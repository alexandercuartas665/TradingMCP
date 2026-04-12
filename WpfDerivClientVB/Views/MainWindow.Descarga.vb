' ============================================================
' MainWindow.Descarga.vb
' Contenido: Tab "Descarga Histórica" completo:
'   - Descarga de velas por rango de fechas
'   - Cálculo y visualización de indicadores técnicos
'     (SMA, EMA, WMA, RSI, Bollinger Bands, MACD)
'   - Log de descarga y barra de progreso
' ============================================================
Imports System.Threading.Tasks
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB

    Partial Public Class MainWindow

        ' ===================================================
        '  TAB DESCARGA HISTORICA
        ' ===================================================
        Private Async Sub btnDescargar_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Dim symbol As String = txtHistSymbol.Text.Trim()
            If String.IsNullOrEmpty(symbol) Then
                System.Windows.MessageBox.Show("Ingresa un simbolo valido (ej: R_100).")
                Return
            End If

            If Not dpDesde.SelectedDate.HasValue OrElse Not dpHasta.SelectedDate.HasValue Then
                System.Windows.MessageBox.Show("Selecciona las fechas Desde y Hasta.")
                Return
            End If

            Dim fromDate As DateTime = dpDesde.SelectedDate.Value
            Dim toDate As DateTime = dpHasta.SelectedDate.Value

            If fromDate >= toDate Then
                System.Windows.MessageBox.Show("La fecha Desde debe ser anterior a Hasta.")
                Return
            End If

            Dim tfItem As System.Windows.Controls.ComboBoxItem = CType(cmbHistTimeframe.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Dim timeframe As String = tfItem.Content.ToString()

            txtDescargaLog.Text = ""
            txtDescargaStatus.Text = String.Format("Iniciando descarga de {0} {1}...", symbol, timeframe)
            txtDescargaContador.Text = ""
            pbDescarga.IsIndeterminate = True
            pbDescarga.Value = 0
            btnDescargar.IsEnabled = False
            btnDetenerDescarga.IsEnabled = True

            AddLogLine(String.Format("=== Descarga iniciada: {0} | {1} | {2} → {3} ===",
                       symbol, timeframe,
                       fromDate.ToString("yyyy-MM-dd"),
                       toDate.ToString("yyyy-MM-dd")))

            Dim sym As String = symbol
            Dim tf As String = timeframe
            Dim fd As DateTime = fromDate
            Dim td As DateTime = toDate

            Await Task.Run(Function() As Task
                               Return _descargaService.DescargarAsync(sym, tf, fd, td,
                                   Sub(progress As DownloadProgress)
                                       Dispatcher.Invoke(Sub() UpdateDownloadProgress(progress))
                                   End Sub)
                           End Function)
        End Sub

        Private Sub btnDetenerDescarga_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            _descargaService.Detener()
            txtDescargaStatus.Text = "Deteniendo descarga..."
            btnDetenerDescarga.IsEnabled = False
        End Sub

        Private Async Sub btnCalcularIndicadores_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Dim symbol As String = txtHistSymbol.Text.Trim()
            If String.IsNullOrEmpty(symbol) Then
                System.Windows.MessageBox.Show("Ingresa un simbolo (ej: R_100).")
                Return
            End If

            Dim tfItem As System.Windows.Controls.ComboBoxItem = CType(cmbHistTimeframe.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Dim timeframe As String = tfItem.Content.ToString()

            Dim period As Integer = 20
            If Not Integer.TryParse(txtMaPeriod.Text.Trim(), period) OrElse period < 2 Then
                System.Windows.MessageBox.Show("El periodo debe ser un numero entero >= 2.")
                Return
            End If

            AppLogger.Info(String.Format("btnCalcularIndicadores: {0} {1} period={2}", symbol, timeframe, period))

            txtDescargaStatus.Text = String.Format("Cargando velas desde BD: {0} {1}...", symbol, timeframe)
            txtDescargaContador.Text = ""
            btnCalcularIndicadores.IsEnabled = False
            txtDescargaLog.Text = ""
            pbDescarga.IsIndeterminate = True

            Dim sym As String = symbol
            Dim tf As String = timeframe
            Dim per As Integer = period

            Try
                Await Task.Run(Async Function() As Task
                                   Try
                                       AppLogger.Info("Cargando velas desde PostgreSQL...")
                                       Dim candles As List(Of CandleModel) = Await _repo.GetCandlesAsync(PLATFORM, sym, tf, limit:=500)
                                       AppLogger.Info(String.Format("Velas cargadas: {0}", candles.Count))

                                       Dispatcher.Invoke(Sub()
                                                             Try
                                                                 pbDescarga.IsIndeterminate = False

                                                                 If candles.Count = 0 Then
                                                                     txtDescargaStatus.Text = "Sin datos en BD para " & sym & " " & tf & ". Descarga historial primero."
                                                                     btnCalcularIndicadores.IsEnabled = True
                                                                     Return
                                                                 End If

                                                                 Dim sb As New System.Text.StringBuilder()
                                                                 Dim showRows As Integer = 15
                                                                 Dim showFrom As Integer = Math.Max(0, candles.Count - showRows)
                                                                 Dim line70 As String = New String("="c, 70)
                                                                 Dim dash70 As String = New String("-"c, 70)

                                                                 sb.AppendLine(line70)
                                                                 sb.AppendLine(String.Format(" INDICADORES: {0} | {1} | Período MA: {2} | {3} velas de BD", sym, tf, per, candles.Count))
                                                                 sb.AppendLine(line70)

                                                                 ' ── 1. Medias Moviles
                                                                 AppLogger.Info("Calculando Medias Moviles...")
                                                                 sb.AppendLine()
                                                                 sb.AppendLine(String.Format("── TREND: Medias Moviles ({0}) ──", per))
                                                                 sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-12} {4,-12}",
                                                           "Hora", "Close", "SMA", "EMA", "WMA"))
                                                                 sb.AppendLine(dash70)

                                                                 Dim smaR = Indicators.MovingAverages.SMA(candles, per)
                                                                 Dim emaR = Indicators.MovingAverages.EMA(candles, per)
                                                                 Dim wmaR = Indicators.MovingAverages.WMA(candles, per)

                                                                 Dim idx As Integer = showFrom
                                                                 Do While idx < candles.Count
                                                                     sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-12} {4,-12}",
                                        candles(idx).OpenTime,
                                        candles(idx).ClosePrice.ToString("F4"),
                                        FormatVal(smaR(idx).IndicatorValue, "F4"),
                                        FormatVal(emaR(idx).IndicatorValue, "F4"),
                                        FormatVal(wmaR(idx).IndicatorValue, "F4")))
                                                                     idx += 1
                                                                 Loop

                                                                 ' ── 2. RSI
                                                                 AppLogger.Info("Calculando RSI(14)...")
                                                                 Dim rsiIndicator As New Indicators.RSI(14)
                                                                 Dim rsiR = rsiIndicator.Calculate(candles)
                                                                 sb.AppendLine()
                                                                 sb.AppendLine("── MOMENTUM: RSI(14) ──")
                                                                 sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-20}",
                                                           "Hora", "Close", "RSI", "Señal"))
                                                                 sb.AppendLine(dash70)
                                                                 idx = showFrom
                                                                 Do While idx < candles.Count
                                                                     Dim rsiVal As String = FormatVal(rsiR(idx).IndicatorValue, "F2")
                                                                     Dim signal As String = ""
                                                                     If rsiR(idx).IndicatorValue.HasValue Then
                                                                         Dim rv As Decimal = rsiR(idx).IndicatorValue.Value
                                                                         If rv > 70 Then signal = "SOBRECOMPRA"
                                                                         If rv < 30 Then signal = "SOBREVENTA"
                                                                         If rv >= 45 AndAlso rv <= 55 Then signal = "NEUTRAL"
                                                                     End If
                                                                     sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-20}",
                                        candles(idx).OpenTime, candles(idx).ClosePrice.ToString("F4"),
                                        rsiVal, signal))
                                                                     idx += 1
                                                                 Loop

                                                                 ' ── 3. Bollinger Bands
                                                                 AppLogger.Info("Calculando Bollinger Bands(20,2)...")
                                                                 Dim bbIndicator As New Indicators.BollingerBands(20, 2D)
                                                                 Dim bbR = bbIndicator.CalculateBands(candles)
                                                                 sb.AppendLine()
                                                                 sb.AppendLine("── VOLATILITY: BB(20,2) ──")
                                                                 sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-12} {4,-12} {5,-10}",
                                                           "Hora", "Close", "Upper", "Middle", "Lower", "BandW%"))
                                                                 sb.AppendLine(dash70)
                                                                 idx = showFrom
                                                                 Do While idx < candles.Count
                                                                     sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-12} {4,-12} {5,-10}",
                                        candles(idx).OpenTime,
                                        candles(idx).ClosePrice.ToString("F4"),
                                        FormatVal(bbR(idx).Upper, "F4"),
                                        FormatVal(bbR(idx).Middle, "F4"),
                                        FormatVal(bbR(idx).Lower, "F4"),
                                        FormatVal(bbR(idx).BandWidth, "F2")))
                                                                     idx += 1
                                                                 Loop

                                                                 ' ── 4. MACD
                                                                 AppLogger.Info("Calculando MACD(12,26,9)...")
                                                                 Dim macdIndicator As New Indicators.MACD(12, 26, 9)
                                                                 Dim macdR = macdIndicator.CalculateMacd(candles)
                                                                 sb.AppendLine()
                                                                 sb.AppendLine("── TREND: MACD(12,26,9) ──")
                                                                 sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-12} {4,-12}",
                                                           "Hora", "Close", "MACD", "Signal", "Histogram"))
                                                                 sb.AppendLine(dash70)
                                                                 idx = showFrom
                                                                 Do While idx < candles.Count
                                                                     Dim hist As String = FormatVal(macdR(idx).Histogram, "F5")
                                                                     If macdR(idx).Histogram.HasValue Then
                                                                         hist = (If(macdR(idx).Histogram.Value > 0, "+", "")) & hist
                                                                     End If
                                                                     sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-12} {4,-12}",
                                        candles(idx).OpenTime,
                                        candles(idx).ClosePrice.ToString("F4"),
                                        FormatVal(macdR(idx).MacdLine, "F5"),
                                        FormatVal(macdR(idx).SignalLine, "F5"),
                                        hist))
                                                                     idx += 1
                                                                 Loop

                                                                 sb.AppendLine()
                                                                 sb.AppendLine(line70)
                                                                 sb.AppendLine(String.Format("Log completo en: {0}", AppLogger.GetLogPath()))

                                                                 txtDescargaLog.Text = sb.ToString()
                                                                 txtDescargaStatus.Text = String.Format(
                                    "Completo: {0} velas | SMA/EMA/WMA({1}) | RSI(14) | BB(20,2) | MACD(12,26,9)", candles.Count, per)
                                                                 txtDescargaContador.Text = String.Format("| Último: {0}", candles.Last().OpenTime)
                                                                 pbDescarga.Value = 100
                                                                 btnCalcularIndicadores.IsEnabled = True
                                                                 AppLogger.Info("Calculo de indicadores completado OK")

                                                             Catch exInner As Exception
                                                                 AppLogger.Error("Error dentro de Dispatcher.Invoke en calculo de indicadores", exInner)
                                                                 pbDescarga.IsIndeterminate = False
                                                                 txtDescargaStatus.Text = "Error al calcular. Ver log: " & AppLogger.GetLogPath()
                                                                 btnCalcularIndicadores.IsEnabled = True
                                                             End Try
                                                         End Sub)

                                   Catch exTask As Exception
                                       AppLogger.Error("Error en Task.Run de calculo de indicadores", exTask)
                                       Dispatcher.Invoke(Sub()
                                                             pbDescarga.IsIndeterminate = False
                                                             txtDescargaStatus.Text = "Error. Revisa log: " & AppLogger.GetLogPath()
                                                             btnCalcularIndicadores.IsEnabled = True
                                                         End Sub)
                                   End Try
                               End Function)

            Catch exOuter As Exception
                AppLogger.Error("Error inesperado en btnCalcularIndicadores_Click", exOuter)
                pbDescarga.IsIndeterminate = False
                txtDescargaStatus.Text = "Error inesperado. Revisa log: " & AppLogger.GetLogPath()
                btnCalcularIndicadores.IsEnabled = True
            End Try
        End Sub

        ''' <summary>Formatea un valor nullable a string con formato o N/A si es Nothing.</summary>
        Private Shared Function FormatVal(v As Nullable(Of Decimal), fmt As String) As String
            If v.HasValue Then Return v.Value.ToString(fmt)
            Return "N/A"
        End Function

        Private Sub UpdateDownloadProgress(progress As DownloadProgress)
            txtDescargaStatus.Text = progress.StatusMsg

            If progress.PageNum > 0 Then
                txtDescargaContador.Text = String.Format("| Pagina {0} | Total: {1} velas",
                                                          progress.PageNum, progress.TotalSaved)
            End If

            AddLogLine(String.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss"), progress.StatusMsg))

            If progress.IsComplete Then
                pbDescarga.IsIndeterminate = False
                pbDescarga.Value = 100

                If progress.HasError Then
                    pbDescarga.Foreground = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#F38BA8"), Color))
                Else
                    pbDescarga.Foreground = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#A6E3A1"), Color))
                End If

                btnDescargar.IsEnabled = True
                btnDetenerDescarga.IsEnabled = False

                Dim sym As String = txtHistSymbol.Text.Trim()
                Dim tfItem As System.Windows.Controls.ComboBoxItem = CType(cmbHistTimeframe.SelectedItem, System.Windows.Controls.ComboBoxItem)
                Dim tf As String = tfItem.Content.ToString()
                Task.Run(Async Function() As Task
                             Dim count As Long = Await _repo.CountAsync(PLATFORM, sym, tf)
                             Dispatcher.Invoke(Sub()
                                                   AddLogLine(String.Format("--- BD: {0} velas totales en {1} {2} ---", count, sym, tf))
                                               End Sub)
                         End Function)
            End If
        End Sub

        Private Sub AddLogLine(text As String)
            If txtDescargaLog.Text.Length > 0 Then
                txtDescargaLog.Text = text & Environment.NewLine & txtDescargaLog.Text
            Else
                txtDescargaLog.Text = text
            End If
        End Sub

    End Class
End Namespace

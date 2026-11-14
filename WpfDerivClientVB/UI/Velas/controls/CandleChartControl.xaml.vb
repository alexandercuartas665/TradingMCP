Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Shapes

' ================================================================
'  CandleChartControl — Pure WPF Canvas renderer
'  Draws: grid lines + labels, candles (body+wick), volume bars,
'         SMA-20, SMA-50, crosshair, price scale, OHLC tooltip
' ================================================================
Partial Public Class CandleChartControl
    Inherits UserControl

    ' ── Data ──────────────────────────────────────────────────────
    Private _candles As List(Of CandleBar)

    ' ── Layout constants ─────────────────────────────────────────
    Private Const PADDING_LEFT As Double = 12
    Private Const PADDING_RIGHT As Double = 60   ' space for price labels
    Private Const PADDING_TOP As Double = 16
    Private Const PADDING_BOTTOM As Double = 50  ' space for volume bars
    Private Const CANDLE_W As Double = 8
    Private Const CANDLE_GAP As Double = 2
    Private Const VOL_HEIGHT As Double = 36       ' max volume bar height

    ' ── Colors ────────────────────────────────────────────────────
    Private ReadOnly _bull As Color = Color.FromRgb(0, 212, 170)
    Private ReadOnly _bear As Color = Color.FromRgb(255, 71, 87)
    Private ReadOnly _wick As Color = Color.FromRgb(90, 106, 109)
    Private ReadOnly _grid As Color = Color.FromRgb(28, 27, 27)
    Private ReadOnly _sma20 As Color = Color.FromRgb(41, 98, 255)
    Private ReadOnly _sma50 As Color = Color.FromRgb(255, 109, 0)
    Private ReadOnly _cyan As Color = Color.FromRgb(76, 214, 251)

    ' ── Mouse tracking ───────────────────────────────────────────
    Private _mouseX As Double = -1
    Private _mouseY As Double = -1

    ' ── Constructor ──────────────────────────────────────────────
    Public Sub New()
        InitializeComponent()
        _candles = New List(Of CandleBar)()
        AddHandler SizeChanged, AddressOf OnSizeChanged
    End Sub

    ' ── Public API ───────────────────────────────────────────────
    Public Sub SetCandles(data As List(Of CandleBar))
        _candles = If(data, New List(Of CandleBar)())
        noDataPanel.Visibility = If(_candles.Count = 0, Visibility.Visible, Visibility.Collapsed)
        RenderChart()
    End Sub

    ' ── Layout event ─────────────────────────────────────────────
    Private Sub OnSizeChanged(sender As Object, e As SizeChangedEventArgs)
        RenderChart()
    End Sub

    ' ── Main render ──────────────────────────────────────────────
    Private Sub RenderChart()
        chartCanvas.Children.Clear()
        crosshairCanvas.Children.Clear()
        If _candles.Count = 0 Then Return

        Dim w As Double = chartCanvas.ActualWidth
        Dim h As Double = chartCanvas.ActualHeight
        If w < 50 OrElse h < 50 Then Return

        Dim chartW As Double = w - PADDING_LEFT - PADDING_RIGHT
        Dim chartH As Double = h - PADDING_TOP - PADDING_BOTTOM

        Dim priceMin As Double, priceMax As Double
        GetPriceRange(priceMin, priceMax)
        Dim pRange As Double = priceMax - priceMin

        If pRange = 0 Then Return

        ' ── Grid ──
        DrawGrid(w, h, chartH, priceMin, priceMax, pRange)

        ' ── SMA lines ──
        Dim sma20 = CalcSMA(20)
        Dim sma50 = CalcSMA(50)
        DrawSMA(sma20, _sma20, w, h, chartH, priceMin, pRange)
        DrawSMA(sma50, _sma50, w, h, chartH, priceMin, pRange)

        ' ── Candles ──
        Dim totalStep As Double = CANDLE_W + CANDLE_GAP
        Dim startX As Double = PADDING_LEFT + chartW - (_candles.Count * totalStep)
        If startX < PADDING_LEFT Then startX = PADDING_LEFT

        For i As Integer = 0 To _candles.Count - 1
            Dim c = _candles(i)
            Dim x As Double = startX + i * totalStep
            DrawCandle(c, x, h, chartH, priceMin, pRange)
        Next

        ' ── Crosshair ──
        If _mouseX >= 0 Then
            DrawCrosshair(w, h, priceMin, pRange)
        End If
    End Sub

    ' ── Grid + Price labels ──────────────────────────────────────
    Private Sub DrawGrid(w As Double, h As Double, chartH As Double,
                         priceMin As Double, priceMax As Double, pRange As Double)
        Dim steps As Integer = 6
        For i As Integer = 0 To steps
            Dim y As Double = PADDING_TOP + chartH * i / steps

            Dim line As New Line() With {
                .X1 = PADDING_LEFT, .Y1 = y,
                .X2 = w - PADDING_RIGHT, .Y2 = y,
                .Stroke = New SolidColorBrush(_grid),
                .StrokeThickness = 1,
                .StrokeDashArray = New DoubleCollection({4, 6})
            }
            chartCanvas.Children.Add(line)

            Dim price As Double = priceMax - pRange * i / steps
            Dim lbl As New TextBlock() With {
                .Text = "$" & price.ToString("N2"),
                .FontFamily = New FontFamily("Consolas"),
                .FontSize = 9,
                .Foreground = New SolidColorBrush(Color.FromRgb(59, 74, 77))
            }
            Canvas.SetLeft(lbl, w - PADDING_RIGHT + 4)
            Canvas.SetTop(lbl, y - 8)
            chartCanvas.Children.Add(lbl)
        Next

        ' Volume separator line
        Dim volLine As New Line() With {
            .X1 = PADDING_LEFT, .Y1 = h - PADDING_BOTTOM,
            .X2 = w - PADDING_RIGHT, .Y2 = h - PADDING_BOTTOM,
            .Stroke = New SolidColorBrush(_grid),
            .StrokeThickness = 1
        }
        chartCanvas.Children.Add(volLine)
    End Sub

    ' ── Single candle ────────────────────────────────────────────
    Private Sub DrawCandle(c As CandleBar, x As Double, h As Double,
                           chartH As Double, priceMin As Double, pRange As Double)
        Dim yHigh  As Double = PriceToY(c.High, h, chartH, priceMin, pRange)
        Dim yLow   As Double = PriceToY(c.Low,  h, chartH, priceMin, pRange)
        Dim yOpen  As Double = PriceToY(c.Open, h, chartH, priceMin, pRange)
        Dim yClose As Double = PriceToY(c.Close, h, chartH, priceMin, pRange)

        Dim isBull As Boolean = c.Close >= c.Open
        Dim bodyColor As Color = If(isBull, _bull, _bear)

        ' Wick
        Dim wick As New Line() With {
            .X1 = x + CANDLE_W / 2, .Y1 = yHigh,
            .X2 = x + CANDLE_W / 2, .Y2 = yLow,
            .Stroke = New SolidColorBrush(_wick),
            .StrokeThickness = 1
        }
        chartCanvas.Children.Add(wick)

        ' Body
        Dim bodyTop As Double = Math.Min(yOpen, yClose)
        Dim bodyH As Double = Math.Max(Math.Abs(yClose - yOpen), 1)
        Dim body As New Rectangle() With {
            .Width = CANDLE_W,
            .Height = bodyH,
            .Fill = New SolidColorBrush(bodyColor)
        }
        Canvas.SetLeft(body, x)
        Canvas.SetTop(body, bodyTop)
        chartCanvas.Children.Add(body)

        ' Volume bar
        Dim volPct As Double = If(c.MaxVolume > 0, c.Volume / c.MaxVolume, 0)
        Dim volH As Double = VOL_HEIGHT * volPct
        Dim volColor As Color = If(isBull,
            Color.FromArgb(70, 0, 212, 170),
            Color.FromArgb(70, 255, 71, 87))
        Dim vbar As New Rectangle() With {
            .Width = CANDLE_W,
            .Height = Math.Max(volH, 1),
            .Fill = New SolidColorBrush(volColor)
        }
        Canvas.SetLeft(vbar, x)
        Canvas.SetTop(vbar, h - PADDING_BOTTOM + 4 - volH)
        chartCanvas.Children.Add(vbar)
    End Sub

    ' ── SMA line ─────────────────────────────────────────────────
    Private Sub DrawSMA(data As Double(), color As Color, w As Double, h As Double,
                        chartH As Double, priceMin As Double, pRange As Double)
        If data Is Nothing OrElse data.Length = 0 Then Return

        Dim totalStep As Double = CANDLE_W + CANDLE_GAP
        Dim nCandles As Integer = _candles.Count
        Dim startX As Double = PADDING_LEFT + (w - PADDING_LEFT - PADDING_RIGHT) -
                               (nCandles * totalStep)
        If startX < PADDING_LEFT Then startX = PADDING_LEFT

        Dim poly As New Polyline() With {
            .Stroke = New SolidColorBrush(color),
            .StrokeThickness = 1.5,
            .StrokeLineJoin = PenLineJoin.Round
        }

        For i As Integer = 0 To data.Length - 1
            If data(i) = 0 Then Continue For
            Dim x As Double = startX + i * totalStep + CANDLE_W / 2
            Dim y As Double = PriceToY(data(i), h, chartH, priceMin, pRange)
            poly.Points.Add(New Point(x, y))
        Next

        chartCanvas.Children.Add(poly)
    End Sub

    ' ── Crosshair ────────────────────────────────────────────────
    Private Sub DrawCrosshair(w As Double, h As Double, priceMin As Double, pRange As Double)
        Dim brushCross As New SolidColorBrush(Color.FromArgb(120, 76, 214, 251))
        Dim dashArr As New DoubleCollection({4, 4})

        Dim vLine As New Line() With {
            .X1 = _mouseX, .Y1 = PADDING_TOP,
            .X2 = _mouseX, .Y2 = h - PADDING_BOTTOM,
            .Stroke = brushCross, .StrokeThickness = 1,
            .StrokeDashArray = dashArr
        }
        crosshairCanvas.Children.Add(vLine)

        Dim hLine As New Line() With {
            .X1 = PADDING_LEFT, .Y1 = _mouseY,
            .X2 = w - PADDING_RIGHT, .Y2 = _mouseY,
            .Stroke = brushCross, .StrokeThickness = 1,
            .StrokeDashArray = dashArr
        }
        crosshairCanvas.Children.Add(hLine)

        ' Price bubble on right axis
        Dim chartH As Double = h - PADDING_TOP - PADDING_BOTTOM
        If pRange > 0 AndAlso _mouseY >= PADDING_TOP AndAlso
           _mouseY <= h - PADDING_BOTTOM Then
            Dim price As Double = priceMax(_mouseY, h, chartH, priceMin, pRange)
            Dim bubble As New Border() With {
                .Background = New SolidColorBrush(_cyan),
                .CornerRadius = New CornerRadius(3),
                .Padding = New Thickness(5, 2, 5, 2)
            }
            Dim lbl As New TextBlock() With {
                .Text = "$" & price.ToString("N2"),
                .FontFamily = New FontFamily("Consolas"),
                .FontSize = 9,
                .FontWeight = FontWeights.Bold,
                .Foreground = New SolidColorBrush(Color.FromRgb(0, 15, 20))
            }
            bubble.Child = lbl
            Canvas.SetLeft(bubble, w - PADDING_RIGHT + 2)
            Canvas.SetTop(bubble, _mouseY - 10)
            crosshairCanvas.Children.Add(bubble)
        End If
    End Sub

    ' ── Mouse events ─────────────────────────────────────────────
    Private Sub ChartCanvas_MouseMove(sender As Object, e As MouseEventArgs)
        Dim pos = e.GetPosition(chartCanvas)
        _mouseX = pos.X
        _mouseY = pos.Y

        ' OHLC tooltip: find nearest candle
        Dim w As Double = chartCanvas.ActualWidth
        Dim h As Double = chartCanvas.ActualHeight
        If _candles.Count = 0 OrElse w < 50 Then Return

        Dim chartW As Double = w - PADDING_LEFT - PADDING_RIGHT
        Dim totalStep As Double = CANDLE_W + CANDLE_GAP
        Dim startX As Double = PADDING_LEFT + chartW - (_candles.Count * totalStep)
        If startX < PADDING_LEFT Then startX = PADDING_LEFT

        Dim idx As Integer = CInt(Math.Floor((_mouseX - startX) / totalStep))
        If idx >= 0 AndAlso idx < _candles.Count Then
            Dim c = _candles(idx)
            txtTooltipTime.Text = c.Time.ToString("MM/dd HH:mm")
            txtTipOpen.Text  = "$" & c.Open.ToString("N2")
            txtTipHigh.Text  = "$" & c.High.ToString("N2")
            txtTipLow.Text   = "$" & c.Low.ToString("N2")
            txtTipClose.Text = "$" & c.Close.ToString("N2")
            txtTipVol.Text   = FormatVolume(c.Volume)
            tooltipPanel.Opacity = 1
        End If

        crosshairCanvas.Children.Clear()
        Dim pm As Double, px As Double
        GetPriceRange(pm, px)
        Dim chartH As Double = h - PADDING_TOP - PADDING_BOTTOM
        DrawCrosshair(w, h, pm, px - pm)
    End Sub

    Private Sub ChartCanvas_MouseLeave(sender As Object, e As MouseEventArgs)
        _mouseX = -1
        _mouseY = -1
        crosshairCanvas.Children.Clear()
        tooltipPanel.Opacity = 0
    End Sub

    ' ── Helpers ──────────────────────────────────────────────────
    Private Function PriceToY(price As Double, h As Double, chartH As Double,
                               priceMin As Double, pRange As Double) As Double
        Return PADDING_TOP + chartH - (price - priceMin) / pRange * chartH
    End Function

    Private Function priceMax(y As Double, h As Double, chartH As Double,
                              priceMin As Double, pRange As Double) As Double
        Return priceMin + (1 - (y - PADDING_TOP) / chartH) * pRange
    End Function

    Private Sub GetPriceRange(ByRef priceMin As Double, ByRef priceMax As Double)
        priceMin = Double.MaxValue
        priceMax = Double.MinValue
        For Each c In _candles
            priceMin = Math.Min(priceMin, c.Low)
            priceMax = Math.Max(priceMax, c.High)
        Next
        Dim pad = (priceMax - priceMin) * 0.08
        priceMin -= pad
        priceMax += pad
    End Sub

    Private Function CalcSMA(period As Integer) As Double()
        Dim result(Math.Max(_candles.Count - 1, 0)) As Double
        For i As Integer = period - 1 To _candles.Count - 1
            Dim sum As Double = 0
            For j As Integer = 0 To period - 1
                sum += _candles(i - j).Close
            Next
            result(i) = sum / period
        Next
        Return result
    End Function

    Private Function FormatVolume(vol As Double) As String
        If vol >= 1_000_000 Then Return (vol / 1_000_000).ToString("F1") & "M"
        If vol >= 1_000 Then Return (vol / 1_000).ToString("F1") & "K"
        Return vol.ToString("F0")
    End Function


End Class


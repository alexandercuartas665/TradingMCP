Imports System.Collections.ObjectModel
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Shapes

Namespace WpfDerivClientVB
    Public Class CandleChart
        Inherits Canvas

        Private _settings As ChartSettings
        Private _candles As ObservableCollection(Of CandleModel)
        Private Const PaddingLeft As Double = 50
        Private Const PaddingRight As Double = 20
        Private Const PaddingTop As Double = 20
        Private Const PaddingBottom As Double = 40
        Private Const CandleWidth As Double = 12
        Private Const CandleGap As Double = 4

        Public Sub New()
            _settings = SettingsManager.Load()
            Background = New SolidColorBrush(ParseColor(_settings.BackgroundColor))
        End Sub

        Public Sub SetData(candles As ObservableCollection(Of CandleModel), settings As ChartSettings)
            _candles = candles
            _settings = settings
            Background = New SolidColorBrush(ParseColor(_settings.BackgroundColor))
            Redraw()
        End Sub

        Public Sub Redraw()
            Me.Children.Clear()
            If _candles Is Nothing OrElse _candles.Count = 0 Then Return

            Dim w As Double = Me.ActualWidth
            Dim h As Double = Me.ActualHeight
            If w < 10 OrElse h < 10 Then Return

            Dim chartW As Double = w - PaddingLeft - PaddingRight
            Dim chartH As Double = h - PaddingTop - PaddingBottom

            ' Calcular rango de precios
            Dim priceMax As Decimal = _candles.Max(Function(c) c.High)
            Dim priceMin As Decimal = _candles.Min(Function(c) c.Low)
            Dim priceRange As Decimal = priceMax - priceMin
            If priceRange = 0 Then priceRange = 1

            ' Cuantas velas caben
            Dim candleStep As Double = CandleWidth + CandleGap
            Dim maxVisible As Integer = CInt(Math.Floor(chartW / candleStep))
            Dim startIdx As Integer = Math.Max(0, _candles.Count - maxVisible)
            Dim visible As New List(Of CandleModel)(_candles.Skip(startIdx))

            ' Dibujar grilla de fondo
            DrawGrid(w, h, chartW, chartH, priceMin, priceMax)

            ' Dibujar cada vela
            For i As Integer = 0 To visible.Count - 1
                Dim c As CandleModel = visible(i)
                Dim x As Double = PaddingLeft + i * candleStep

                Dim isBull As Boolean = c.ClosePrice >= c.OpenPrice
                Dim bodyBrush As New SolidColorBrush(If(isBull, ParseColor(_settings.BullishColor), ParseColor(_settings.BearishColor)))
                Dim wickBrush As New SolidColorBrush(ParseColor(_settings.WickColor))

                ' Calcular posiciones Y (invertidas, el precio mayor = arriba)
                Dim yHigh As Double = PaddingTop + chartH * CDbl((priceMax - c.High) / priceRange)
                Dim yLow As Double = PaddingTop + chartH * CDbl((priceMax - c.Low) / priceRange)
                Dim yOpen As Double = PaddingTop + chartH * CDbl((priceMax - c.OpenPrice) / priceRange)
                Dim yClose As Double = PaddingTop + chartH * CDbl((priceMax - c.ClosePrice) / priceRange)

                Dim bodyTop As Double = Math.Min(yOpen, yClose)
                Dim bodyBot As Double = Math.Max(yOpen, yClose)
                Dim bodyHeight As Double = Math.Max(1, bodyBot - bodyTop)

                ' Mecha (wick) superior/inferior
                Dim wick As New Line() With {
                    .X1 = x + CandleWidth / 2, .Y1 = yHigh,
                    .X2 = x + CandleWidth / 2, .Y2 = yLow,
                    .Stroke = wickBrush, .StrokeThickness = 1.5
                }
                Me.Children.Add(wick)

                ' Cuerpo de la vela
                Dim body As New Rectangle() With {
                    .Width = CandleWidth, .Height = bodyHeight,
                    .Fill = bodyBrush
                }
                Canvas.SetLeft(body, x)
                Canvas.SetTop(body, bodyTop)
                Me.Children.Add(body)

                ' Etiqueta del precio close en la última vela
                If i = visible.Count - 1 Then
                    Dim lbl As New TextBlock() With {
                        .Text = c.ClosePrice.ToString("F5"),
                        .Foreground = bodyBrush,
                        .FontSize = 10
                    }
                    Canvas.SetLeft(lbl, x + CandleWidth + 2)
                    Canvas.SetTop(lbl, yClose - 8)
                    Me.Children.Add(lbl)
                End If
            Next

            ' Eje de precios (etiquetas izquierda)
            DrawPriceAxis(h, chartH, priceMin, priceMax)
        End Sub

        Private Sub DrawGrid(w As Double, h As Double, chartW As Double, chartH As Double,
                              priceMin As Decimal, priceMax As Decimal)
            Dim gridColor As New SolidColorBrush(ParseColor(_settings.GridColor))
            Dim levels As Integer = 5
            For i As Integer = 0 To levels
                Dim y As Double = PaddingTop + chartH * i / levels
                Dim gridLine As New Line() With {
                    .X1 = PaddingLeft, .Y1 = y, .X2 = w - PaddingRight, .Y2 = y,
                    .Stroke = gridColor, .StrokeThickness = 0.5,
                    .StrokeDashArray = New DoubleCollection({4, 4})
                }
                Me.Children.Add(gridLine)
            Next
        End Sub

        Private Sub DrawPriceAxis(h As Double, chartH As Double, priceMin As Decimal, priceMax As Decimal)
            Dim labelBrush As New SolidColorBrush(Colors.LightGray)
            Dim levels As Integer = 5
            For i As Integer = 0 To levels
                Dim price As Decimal = priceMax - (priceMax - priceMin) * i / levels
                Dim y As Double = PaddingTop + chartH * i / levels
                Dim lbl As New TextBlock() With {
                    .Text = price.ToString("F4"),
                    .Foreground = labelBrush,
                    .FontSize = 9
                }
                Canvas.SetLeft(lbl, 2)
                Canvas.SetTop(lbl, y - 8)
                Me.Children.Add(lbl)
            Next
        End Sub

        Private Shared Function ParseColor(hex As String) As Color
            Try
                Dim c As System.Windows.Media.Color = CType(ColorConverter.ConvertFromString(hex), System.Windows.Media.Color)
                Return c
            Catch
                Return Colors.Gray
            End Try
        End Function
    End Class
End Namespace

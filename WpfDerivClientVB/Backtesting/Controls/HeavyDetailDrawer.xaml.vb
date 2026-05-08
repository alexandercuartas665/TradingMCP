' ============================================================
' HeavyDetailDrawer.xaml.vb
'
' UserControl que se muestra como drawer al hacer click en una fila
' del grid central. Carga las metricas detalladas + dibuja charts en
' Canvas (sin libs externas).
' ============================================================
Imports System.Collections.Generic
Imports System.Linq
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Shapes
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting

Partial Public Class HeavyDetailDrawer
    Inherits UserControl

    ''' <summary>Disparado cuando el usuario clickea ✕ — el contenedor lo escucha para cerrar.</summary>
    Public Event Cerrar As Action

    Private _job As HeavyTestJob = Nothing

    Public Sub New()
        InitializeComponent()
    End Sub

    ''' <summary>Carga toda la data del job en los tabs. Se llama desde la ventana padre.</summary>
    Public Sub Cargar(job As HeavyTestJob)
        _job = job
        If job Is Nothing OrElse job.Resultado Is Nothing OrElse job.Resultado.Sesion Is Nothing Then
            txtTitulo.Text = "—"
            txtSub.Text = ""
            Return
        End If

        Dim ses = job.Resultado.Sesion
        Dim ops = job.Resultado.Operaciones

        txtTitulo.Text = String.Format("{0} · {1} / {2}", job.Estrategia, job.Symbol, job.Timeframe)
        txtSub.Text = String.Format("conf: {0}%   pay: {1}%   dur: {2} velas   cap: ${3:N0}   ops: {4}",
                                    job.Confianza, job.Payout, job.Duracion, job.Capital, ses.TotalOperaciones)

        ' Overview
        ovTotalOps.Text = ses.TotalOperaciones.ToString()
        ovWin.Text = (ses.WinRate * 100D).ToString("F1") & "%"
        ovWin.Foreground = If(ses.WinRate >= 0.5D, Brushes.LightGreen, Brushes.Salmon)
        ovPnl.Text = ses.ProfitLossTotal.ToString("+0.00;-0.00;0.00")
        ovPnl.Foreground = If(ses.ProfitLossTotal >= 0D, Brushes.LightGreen, Brushes.Salmon)
        ovDD.Text = ses.MaxDrawdown.ToString("F1") & "%"
        ovDD.Foreground = Brushes.Salmon

        ' Riesgo tab stats
        rkDD.Text = ses.MaxDrawdown.ToString("F2") & "%"
        rkSharpe.Text = HeavyStatsCalculator.Sharpe(ops).ToString("F2")
        rkPF.Text = HeavyStatsCalculator.ProfitFactor(ops).ToString("F2")
        Dim er As Decimal = If(ops Is Nothing OrElse ops.Count = 0, 0D, ops.Average(Function(o) o.ProfitLoss))
        rkER.Text = er.ToString("+0.00;-0.00;0.00")

        ' Operaciones tab
        Dim filas As New List(Of OpRowDto)()
        Dim idx As Integer = 1
        For Each o In ops.OrderBy(Function(x) x.EpochEntrada)
            filas.Add(New OpRowDto With {
                .Idx = idx,
                .AperturaStr = HeavyStatsCalculator.EpochToUtc(o.EpochEntrada).ToLocalTime().ToString("dd/MM/yy HH:mm"),
                .CierreStr = If(o.EpochSalida > 0,
                                HeavyStatsCalculator.EpochToUtc(o.EpochSalida).ToLocalTime().ToString("dd/MM/yy HH:mm"),
                                ""),
                .Tipo = o.Tipo,
                .EntradaStr = o.PrecioEntrada.ToString("F5"),
                .SalidaStr = If(o.PrecioSalida > 0D, o.PrecioSalida.ToString("F5"), ""),
                .Estado = o.Estado,
                .PnlStr = o.ProfitLoss.ToString("+0.00;-0.00;0.00")
            })
            idx += 1
        Next
        dgOps.ItemsSource = filas

        ' Charts: equity + drawdown
        Dim equity = HeavyStatsCalculator.EquityCurve(ops, job.Capital)
        DibujarLinea(canvasEquity, equity.Select(Function(p) CDbl(p.Equity)).ToList(),
                     Color.FromRgb(&H5E, &HE0, &HD0), True)

        Dim dd = HeavyStatsCalculator.DrawdownRolling(equity)
        DibujarLinea(canvasDD, dd.Select(Function(p) CDbl(p.DDPct)).ToList(),
                     Color.FromRgb(&HFF, &H5C, &H7A), False)

        ' Charts semanal
        Dim porSem = HeavyStatsCalculator.OpsPorSemana(ops).OrderBy(Function(kv) kv.Key).ToList()
        DibujarBarras(canvasSemanal,
                      porSem.Select(Function(kv) CDbl(kv.Value)).ToList(),
                      porSem.Select(Function(kv) kv.Key).ToList(),
                      Color.FromRgb(&H8B, &H7D, &HFF))

        Dim winSem = HeavyStatsCalculator.WinRateSemanal(ops).OrderBy(Function(kv) kv.Key).ToList()
        DibujarLinea(canvasWinSem, winSem.Select(Function(kv) CDbl(kv.Value * 100)).ToList(),
                     Color.FromRgb(&H3F, &HD1, &H7A), True)

        ' Histograma
        Dim hist = HeavyStatsCalculator.DistribucionPnl(ops, 20)
        DibujarBarras(canvasHist,
                      hist.Select(Function(b) CDbl(b.Count)).ToList(),
                      hist.Select(Function(b) b.Centro.ToString("F2")).ToList(),
                      Color.FromRgb(&HFF, &HB4, &H54))
    End Sub

    Private Sub btnCerrar_Click(sender As Object, e As RoutedEventArgs)
        RaiseEvent Cerrar()
    End Sub

    ''' <summary>Cambia de tab al click en el header.</summary>
    Private Sub tabClick(sender As Object, e As RoutedEventArgs)
        Dim btn = TryCast(sender, Button)
        If btn Is Nothing Then Return
        Dim t = btn.Tag?.ToString()

        ' Reset estilo de todos los tabs
        For Each c In tabsBar.Children.OfType(Of Button)()
            c.Foreground = New SolidColorBrush(Color.FromRgb(&HAA, &HB3, &HC5))
            c.BorderThickness = New Thickness(0)
        Next
        btn.Foreground = New SolidColorBrush(Color.FromRgb(&H5E, &HE0, &HD0))
        btn.BorderThickness = New Thickness(0, 0, 0, 2)
        btn.BorderBrush = New SolidColorBrush(Color.FromRgb(&H5E, &HE0, &HD0))

        contOverview.Visibility = If(t = "overview", Visibility.Visible, Visibility.Collapsed)
        contOps.Visibility = If(t = "ops", Visibility.Visible, Visibility.Collapsed)
        contSemanal.Visibility = If(t = "weekly", Visibility.Visible, Visibility.Collapsed)
        contRiesgo.Visibility = If(t = "risk", Visibility.Visible, Visibility.Collapsed)
    End Sub

    ' ── DIBUJO DE CHARTS EN CANVAS (sin libs externas) ───────────────────

    ''' <summary>Dibuja una linea polilinea en el canvas, escalando los valores.</summary>
    Private Sub DibujarLinea(c As Canvas, valores As List(Of Double), color As Color,
                             rellenarBajo As Boolean)
        c.Children.Clear()
        If valores Is Nothing OrElse valores.Count = 0 Then Return
        Dim w As Double = If(c.ActualWidth > 0, c.ActualWidth, 800)
        Dim h As Double = If(c.ActualHeight > 0, c.ActualHeight, 200)
        Dim padX As Double = 8 : Dim padY As Double = 8
        Dim plotW = w - 2 * padX
        Dim plotH = h - 2 * padY
        Dim mn = valores.Min()
        Dim mx = valores.Max()
        If Math.Abs(mx - mn) < 0.0001 Then mx += 1

        Dim puntos As New PointCollection()
        Dim n = valores.Count
        For i As Integer = 0 To n - 1
            Dim x = padX + If(n > 1, plotW * i / (n - 1), 0)
            Dim y = padY + plotH * (1 - (valores(i) - mn) / (mx - mn))
            puntos.Add(New Point(x, y))
        Next

        ' Grid horizontal de referencia (4 lineas)
        For i As Integer = 0 To 3
            Dim yg = padY + plotH * i / 3
            Dim ln As New Line With {
                .X1 = padX, .X2 = w - padX, .Y1 = yg, .Y2 = yg,
                .Stroke = New SolidColorBrush(Color.FromRgb(&H1F, &H26, &H38)),
                .StrokeThickness = 0.5
            }
            c.Children.Add(ln)
        Next

        ' Polilinea principal
        Dim pl As New Polyline With {
            .Stroke = New SolidColorBrush(color),
            .StrokeThickness = 1.6,
            .Points = puntos
        }
        c.Children.Add(pl)

        ' Relleno opcional (area bajo la curva)
        If rellenarBajo Then
            Dim fill As New PointCollection(puntos)
            fill.Add(New Point(puntos.Last().X, padY + plotH))
            fill.Add(New Point(puntos.First().X, padY + plotH))
            Dim poly As New Polygon With {
                .Points = fill,
                .Fill = New SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)),
                .Stroke = Nothing
            }
            c.Children.Insert(c.Children.Count - 1, poly)
        End If
    End Sub

    ''' <summary>Dibuja barras verticales escaladas al canvas.</summary>
    Private Sub DibujarBarras(c As Canvas, valores As List(Of Double),
                               etiquetas As List(Of String), color As Color)
        c.Children.Clear()
        If valores Is Nothing OrElse valores.Count = 0 Then Return
        Dim w As Double = If(c.ActualWidth > 0, c.ActualWidth, 800)
        Dim h As Double = If(c.ActualHeight > 0, c.ActualHeight, 200)
        Dim padX As Double = 8 : Dim padY As Double = 18
        Dim plotW = w - 2 * padX
        Dim plotH = h - 2 * padY
        Dim mx = valores.Max()
        If mx <= 0 Then mx = 1

        Dim n = valores.Count
        Dim espacio = plotW / n
        Dim ancho = Math.Max(1, espacio * 0.7)

        For i As Integer = 0 To n - 1
            Dim altoBarra = plotH * (valores(i) / mx)
            Dim x = padX + i * espacio + (espacio - ancho) / 2
            Dim y = padY + plotH - altoBarra
            Dim r As New Rectangle With {
                .Width = ancho,
                .Height = altoBarra,
                .Fill = New SolidColorBrush(Color.FromArgb(220, color.R, color.G, color.B))
            }
            Canvas.SetLeft(r, x)
            Canvas.SetTop(r, y)
            c.Children.Add(r)
        Next
    End Sub

    Public Class OpRowDto
        Public Property Idx As Integer
        Public Property AperturaStr As String
        Public Property CierreStr As String
        Public Property Tipo As String
        Public Property EntradaStr As String
        Public Property SalidaStr As String
        Public Property Estado As String
        Public Property PnlStr As String
    End Class

End Class

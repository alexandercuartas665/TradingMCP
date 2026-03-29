Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media

Partial Public Class CandleTransactionsControl
    Inherits UserControl

    Private _wins As Integer = 0
    Private _losses As Integer = 0
    Private _totalPnl As Decimal = 0

    Public Sub New()
        InitializeComponent()
    End Sub

    Public Sub AddTransaction(symbol As String, direction As String,
                               amount As Decimal, profit As Decimal,
                               timestamp As DateTime)
        _wins   += If(profit >= 0, 1, 0)
        _losses += If(profit < 0, 1, 0)
        _totalPnl += profit

        ' Row
        Dim bdr As New Border() With {
            .Background = New SolidColorBrush(Color.FromRgb(28, 27, 27)),
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(10, 5, 10, 5),
            .Margin = New Thickness(2, 0, 2, 3)
        }

        Dim isBuy As Boolean = direction.ToUpper() = "BUY"
        Dim profitColor As Color = If(profit >= 0,
            Color.FromRgb(0, 212, 170),
            Color.FromRgb(255, 71, 87))
        Dim dirColor As Color = If(isBuy,
            Color.FromRgb(0, 212, 170),
            Color.FromRgb(255, 71, 87))

        Dim grid As New Grid()
        grid.ColumnDefinitions.Add(New ColumnDefinition() With {.Width = New GridLength(1, GridUnitType.Star)})
        grid.ColumnDefinitions.Add(New ColumnDefinition() With {.Width = New GridLength(40)})
        grid.ColumnDefinitions.Add(New ColumnDefinition() With {.Width = New GridLength(70)})
        grid.ColumnDefinitions.Add(New ColumnDefinition() With {.Width = New GridLength(70)})

        Dim sym As New TextBlock() With {
            .Text = symbol,
            .FontSize = 10, .FontWeight = FontWeights.Bold,
            .FontFamily = New FontFamily("Consolas"),
            .Foreground = New SolidColorBrush(Color.FromRgb(224, 230, 237)),
            .VerticalAlignment = VerticalAlignment.Center
        }
        Grid.SetColumn(sym, 0)

        Dim dir As New TextBlock() With {
            .Text = If(isBuy, "▲ BUY", "▼ SELL"),
            .FontSize = 9, .FontWeight = FontWeights.Bold,
            .FontFamily = New FontFamily("Consolas"),
            .Foreground = New SolidColorBrush(dirColor),
            .VerticalAlignment = VerticalAlignment.Center
        }
        Grid.SetColumn(dir, 1)

        Dim amt As New TextBlock() With {
            .Text = "$" & amount.ToString("N2"),
            .FontSize = 9,
            .FontFamily = New FontFamily("Consolas"),
            .Foreground = New SolidColorBrush(Color.FromRgb(90, 106, 109)),
            .HorizontalAlignment = HorizontalAlignment.Right,
            .VerticalAlignment = VerticalAlignment.Center
        }
        Grid.SetColumn(amt, 2)

        Dim pnl As New TextBlock() With {
            .Text = (If(profit >= 0, "+", "")) & profit.ToString("N2"),
            .FontSize = 9, .FontWeight = FontWeights.Bold,
            .FontFamily = New FontFamily("Consolas"),
            .Foreground = New SolidColorBrush(profitColor),
            .HorizontalAlignment = HorizontalAlignment.Right,
            .VerticalAlignment = VerticalAlignment.Center
        }
        Grid.SetColumn(pnl, 3)

        grid.Children.Add(sym)
        grid.Children.Add(dir)
        grid.Children.Add(amt)
        grid.Children.Add(pnl)
        bdr.Child = grid

        pnlTransactions.Children.Insert(0, bdr)
        Do While pnlTransactions.Children.Count > 20
            pnlTransactions.Children.RemoveAt(pnlTransactions.Children.Count - 1)
        Loop

        UpdateSummary()
    End Sub

    Private Sub UpdateSummary()
        Dim total = _wins + _losses
        txtTxCount.Text = total & " ops hoy"
        txtWins.Text    = _wins.ToString()
        txtLosses.Text  = _losses.ToString()
        txtTotal.Text   = total.ToString()

        Dim pnlSign As String = If(_totalPnl >= 0, "+", "")
        txtPnl.Text = "P&L: " & pnlSign & _totalPnl.ToString("N2")
        txtPnl.Foreground = New SolidColorBrush(
            If(_totalPnl >= 0,
               Color.FromRgb(76, 214, 251),
               Color.FromRgb(255, 71, 87)))

        If total > 0 Then
            Dim wr As Double = _wins / total * 100
            txtWinRate.Text = "WR: " & wr.ToString("F0") & "%"
            txtWinRate.Foreground = New SolidColorBrush(
                If(wr >= 50, Color.FromRgb(0, 212, 170), Color.FromRgb(255, 71, 87)))
        End If
    End Sub

End Class

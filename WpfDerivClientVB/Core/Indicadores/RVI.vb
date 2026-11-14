Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Relative Vigor Index (RVI)
    ''' Mide la convicción de la tendencia comparando precios de cierre relativos a apertura.
    ''' </summary>
    Public Class RVI
        Implements IIndicator

        Private ReadOnly _period As Integer

        Public Sub New(Optional period As Integer = 10)
            _period = period
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("RVI({0})", _period)
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return "Relative Vigor Index. Mide energía/fuerza de la tendencia comparando close-open vs high-low."
            End Get
        End Property

        Public ReadOnly Property Category As String Implements IIndicator.Category
            Get
                Return "Momentum"
            End Get
        End Property

        Public Function Calculate(candles As IEnumerable(Of CandleModel)) As List(Of IndicatorResult) Implements IIndicator.Calculate
            Dim candleList = candles.ToList()
            Dim results As New List(Of IndicatorResult)(candleList.Count)
            Dim labelStr As String = Me.Name

            Dim numList As New List(Of Decimal)
            Dim denList As New List(Of Decimal)

            For i As Integer = 0 To candleList.Count - 1
                If i < 3 Then
                    numList.Add(0D)
                    denList.Add(0D)
                Else
                    Dim c0 = candleList(i).ClosePrice,  o0 = candleList(i).OpenPrice
                    Dim c1 = candleList(i-1).ClosePrice, o1 = candleList(i-1).OpenPrice
                    Dim c2 = candleList(i-2).ClosePrice, o2 = candleList(i-2).OpenPrice
                    Dim c3 = candleList(i-3).ClosePrice, o3 = candleList(i-3).OpenPrice

                    Dim h0 = candleList(i).High,  l0 = candleList(i).Low
                    Dim h1 = candleList(i-1).High, l1 = candleList(i-1).Low
                    Dim h2 = candleList(i-2).High, l2 = candleList(i-2).Low
                    Dim h3 = candleList(i-3).High, l3 = candleList(i-3).Low

                    Dim num = ((c0 - o0) + 2D * (c1 - o1) + 2D * (c2 - o2) + (c3 - o3)) / 6D
                    Dim den = ((h0 - l0) + 2D * (h1 - l1) + 2D * (h2 - l2) + (h3 - l3)) / 6D
                    numList.Add(num)
                    denList.Add(den)
                End If
            Next

            For i As Integer = 0 To candleList.Count - 1
                Dim current As CandleModel = candleList(i)
                Dim result As New IndicatorResult() With {
                    .Epoch = current.Epoch,
                    .OpenTime = current.OpenTime,
                    .Label = labelStr
                }

                If i < _period + 3 - 1 Then
                    result.IndicatorValue = Nothing
                Else
                    Dim smaNum = numList.Skip(i - _period + 1).Take(_period).Sum() ' Normally sum is used then division cancels out
                    Dim smaDen = denList.Skip(i - _period + 1).Take(_period).Sum()

                    If smaDen = 0D Then
                        result.IndicatorValue = 0D
                    Else
                        Dim rviVal = smaNum / smaDen
                        result.IndicatorValue = rviVal

                        ' Signal is an SMA 4 of RVI (Wait, simple smooth)
                    End If
                End If

                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

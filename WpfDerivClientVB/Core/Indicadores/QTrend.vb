Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Q-Trend
    ''' Indicador personalizado para filtrar ruido y mostrar macro tendencias (similar a MA cross o Supertrend lento).
    ''' </summary>
    Public Class QTrend
        Implements IIndicator

        Private ReadOnly _period As Integer

        Public Sub New(Optional period As Integer = 21)
            _period = period
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("QTrend({0})", _period)
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return "Q-Trend: Suavizado inteligente de dirección tendencial a largo plazo."
            End Get
        End Property

        Public ReadOnly Property Category As String Implements IIndicator.Category
            Get
                Return "Trend"
            End Get
        End Property

        Public Function Calculate(candles As IEnumerable(Of CandleModel)) As List(Of IndicatorResult) Implements IIndicator.Calculate
            Dim candleList = candles.ToList()
            Dim results As New List(Of IndicatorResult)(candleList.Count)
            Dim labelStr As String = Me.Name

            Dim ema1List As New List(Of Decimal)

            ' Smooth 1: EMA(Close)
            Dim k As Decimal = 2D / (_period + 1D)
            Dim ema As Decimal = 0

            For i As Integer = 0 To candleList.Count - 1
                Dim current = candleList(i)
                If i = 0 Then
                    ema = current.ClosePrice
                Else
                    ema = (current.ClosePrice - ema) * k + ema
                End If
                ema1List.Add(ema)

                Dim result As New IndicatorResult() With {
                    .Epoch = current.Epoch,
                    .OpenTime = current.OpenTime,
                    .Label = labelStr
                }

                If i < _period Then
                    result.IndicatorValue = Nothing
                Else
                    ' Evaluar Trend comparando Highs/Lows con EMA
                    Dim isTrendUp = If(current.ClosePrice > ema, 1D, -1D)
                    result.IndicatorValue = ema
                    result.AdditionalValues.Add("Direction", isTrendUp)
                End If

                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

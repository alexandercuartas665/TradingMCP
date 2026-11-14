Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' SSL Channel (Semaphore)
    ''' Fundamental indicator combining SMA of Highs and Lows to determine short-term trend.
    ''' </summary>
    Public Class SSLChannel
        Implements IIndicator

        Private ReadOnly _period As Integer

        Public Sub New(Optional period As Integer = 10)
            _period = period
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("SSL({0})", _period)
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return "SSL Channel: medias móviles de altos y bajos que alternan soporte/resistencia según tendencia."
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

            Dim isUpTrend As Boolean = False

            Dim smaHighList As New List(Of Decimal)
            Dim smaLowList As New List(Of Decimal)

            For i As Integer = 0 To candleList.Count - 1
                Dim current As CandleModel = candleList(i)
                Dim result As New IndicatorResult() With {
                    .Epoch = current.Epoch,
                    .OpenTime = current.OpenTime,
                    .Label = labelStr
                }

                If i < _period - 1 Then
                    result.IndicatorValue = Nothing
                Else
                    Dim window = candleList.Skip(i - _period + 1).Take(_period)
                    Dim smaHigh = window.Average(Function(c) c.High)
                    Dim smaLow = window.Average(Function(c) c.Low)

                    If current.ClosePrice > smaHigh Then
                        isUpTrend = True
                    ElseIf current.ClosePrice < smaLow Then
                        isUpTrend = False
                    End If

                    Dim sslUp = If(isUpTrend, smaLow, smaHigh)
                    Dim sslDown = If(isUpTrend, smaHigh, smaLow)

                    result.IndicatorValue = sslUp
                    result.AdditionalValues.Add("SSL_Down", sslDown)
                    result.AdditionalValues.Add("Trend", If(isUpTrend, 1D, -1D))
                End If

                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

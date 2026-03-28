Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Supertrend
    ''' Indicador seguidor de tendencia, basado en el ATR y precio medio.
    ''' </summary>
    Public Class Supertrend
        Implements IIndicator

        Private ReadOnly _period As Integer
        Private ReadOnly _multiplier As Decimal

        Public Sub New(Optional period As Integer = 10, Optional multiplier As Decimal = 3.0D)
            _period = period
            _multiplier = multiplier
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("Supertrend({0},{1})", _period, _multiplier)
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return "Indicador de tendencia principal. Usado para identificar reversals."
            End Get
        End Property

        Public ReadOnly Property Category As String Implements IIndicator.Category
            Get
                Return "Trend"
            End Get
        End Property

        Public Function Calculate(candles As IEnumerable(Of CandleModel)) As List(Of IndicatorResult) Implements IIndicator.Calculate
            Dim atrIndicator As New ATR(_period)
            Dim atrResults = atrIndicator.Calculate(candles)
            Dim candleList = candles.ToList()

            Dim results As New List(Of IndicatorResult)(candleList.Count)
            Dim labelStr As String = Me.Name

            Dim isUpTrend As Boolean = True
            Dim finalUpperband As Decimal = 0
            Dim finalLowerband As Decimal = 0
            Dim superTrendVal As Decimal = 0

            For i As Integer = 0 To candleList.Count - 1
                Dim current As CandleModel = candleList(i)
                Dim hl2 = (current.High + current.Low) / 2D
                Dim atrVal = atrResults(i).IndicatorValue

                Dim result As New IndicatorResult() With {
                    .Epoch = current.Epoch,
                    .OpenTime = current.OpenTime,
                    .Label = labelStr
                }

                If i = 0 OrElse Not atrVal.HasValue Then
                    result.IndicatorValue = Nothing
                    results.Add(result)
                    Continue For
                End If

                Dim basicUpperband = hl2 + (_multiplier * atrVal.Value)
                Dim basicLowerband = hl2 - (_multiplier * atrVal.Value)

                Dim prev = candleList(i - 1)
                
                If i = _period Then
                    finalUpperband = basicUpperband
                    finalLowerband = basicLowerband
                    isUpTrend = current.ClosePrice > finalUpperband
                    superTrendVal = If(isUpTrend, finalLowerband, finalUpperband)
                Else
                    Dim prevFinalUpperband = finalUpperband
                    Dim prevFinalLowerband = finalLowerband

                    If basicUpperband < prevFinalUpperband OrElse prev.ClosePrice > prevFinalUpperband Then
                        finalUpperband = basicUpperband
                    Else
                        finalUpperband = prevFinalUpperband
                    End If

                    If basicLowerband > prevFinalLowerband OrElse prev.ClosePrice < prevFinalLowerband Then
                        finalLowerband = basicLowerband
                    Else
                        finalLowerband = prevFinalLowerband
                    End If

                    If superTrendVal = prevFinalUpperband AndAlso current.ClosePrice <= finalUpperband Then
                        isUpTrend = False
                    ElseIf superTrendVal = prevFinalUpperband AndAlso current.ClosePrice >= finalUpperband Then
                        isUpTrend = True
                    ElseIf superTrendVal = prevFinalLowerband AndAlso current.ClosePrice >= finalLowerband Then
                        isUpTrend = True
                    ElseIf superTrendVal = prevFinalLowerband AndAlso current.ClosePrice <= finalLowerband Then
                        isUpTrend = False
                    End If

                    superTrendVal = If(isUpTrend, finalLowerband, finalUpperband)
                End If

                result.IndicatorValue = superTrendVal
                result.AdditionalValues.Add("Trend", If(isUpTrend, 1D, -1D))
                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

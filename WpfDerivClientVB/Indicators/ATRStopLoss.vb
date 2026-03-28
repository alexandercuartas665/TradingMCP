Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' ATR Trailing Stop Loss
    ''' Diferente a ATR normal, muestra niveles de Stop Loss trailing usando un multiplicador
    ''' sobre el ATR.
    ''' </summary>
    Public Class ATRStopLoss
        Implements IIndicator

        Private ReadOnly _period As Integer
        Private ReadOnly _multiplier As Decimal

        ''' <param name="period">Períodos de ATR.</param>
        ''' <param name="multiplier">Multiplicador del ATR para el trailing stop.</param>
        Public Sub New(Optional period As Integer = 14, Optional multiplier As Decimal = 3.0D)
            _period = period
            _multiplier = multiplier
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("ATR SL({0}, {1})", _period, _multiplier)
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return "Trailing Stop Loss basado en ATR."
            End Get
        End Property

        Public ReadOnly Property Category As String Implements IIndicator.Category
            Get
                Return "Volatility"
            End Get
        End Property

        Public Function Calculate(candles As IEnumerable(Of CandleModel)) As List(Of IndicatorResult) Implements IIndicator.Calculate
            Dim atrIndicator As New ATR(_period)
            Dim atrResults = atrIndicator.Calculate(candles)
            Dim candleList = candles.ToList()

            Dim results As New List(Of IndicatorResult)(candleList.Count)
            Dim labelStr As String = Me.Name

            Dim trailingStop As Decimal = 0
            Dim isLong As Boolean = True

            For i As Integer = 0 To candleList.Count - 1
                Dim current As CandleModel = candleList(i)
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

                Dim prev As CandleModel = candleList(i - 1)
                
                If i = _period Then
                    isLong = current.ClosePrice > prev.ClosePrice
                    If isLong Then
                        trailingStop = current.High - (atrVal.Value * _multiplier)
                    Else
                        trailingStop = current.Low + (atrVal.Value * _multiplier)
                    End If
                Else
                    Dim prevTrailingStop = trailingStop

                    If isLong Then
                        Dim tempStop = current.High - (atrVal.Value * _multiplier)
                        If tempStop > prevTrailingStop Then trailingStop = tempStop
                        
                        If current.ClosePrice < trailingStop Then
                            isLong = False
                            trailingStop = current.Low + (atrVal.Value * _multiplier)
                        End If
                    Else
                        Dim tempStop = current.Low + (atrVal.Value * _multiplier)
                        If tempStop < prevTrailingStop Then trailingStop = tempStop
                        
                        If current.ClosePrice > trailingStop Then
                            isLong = True
                            trailingStop = current.High - (atrVal.Value * _multiplier)
                        End If
                    End If
                End If

                result.IndicatorValue = trailingStop
                result.AdditionalValues.Add("IsLong", If(isLong, 1D, -1D))
                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

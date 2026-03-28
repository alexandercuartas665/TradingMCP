Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Average True Range (ATR)
    ''' Mide la volatilidad del mercado descomponiendo el rango completo del precio de un activo para ese período.
    ''' </summary>
    Public Class ATR
        Implements IIndicator

        Private ReadOnly _period As Integer

        Public Sub New(Optional period As Integer = 14)
            _period = period
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("ATR({0})", _period)
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return String.Format("Average True Range de {0} períodos. Mide volatilidad.", _period)
            End Get
        End Property

        Public ReadOnly Property Category As String Implements IIndicator.Category
            Get
                Return "Volatility"
            End Get
        End Property

        Public Function Calculate(candles As IEnumerable(Of CandleModel)) As List(Of IndicatorResult) Implements IIndicator.Calculate
            Dim candleList As List(Of CandleModel) = candles.ToList()
            Dim results As New List(Of IndicatorResult)(candleList.Count)
            Dim labelStr As String = Me.Name

            Dim trList As New List(Of Decimal)
            Dim atr As Decimal = 0

            For i As Integer = 0 To candleList.Count - 1
                Dim current As CandleModel = candleList(i)
                Dim result As New IndicatorResult() With {
                    .Epoch = current.Epoch,
                    .OpenTime = current.OpenTime,
                    .Label = labelStr
                }

                If i = 0 Then
                    trList.Add(current.High - current.Low)
                    result.IndicatorValue = Nothing
                Else
                    Dim prev As CandleModel = candleList(i - 1)
                    Dim highLow = current.High - current.Low
                    Dim highClose = Math.Abs(current.High - prev.ClosePrice)
                    Dim lowClose = Math.Abs(current.Low - prev.ClosePrice)
                    Dim tr = Math.Max(highLow, Math.Max(highClose, lowClose))
                    trList.Add(tr)

                    If i < _period Then
                        result.IndicatorValue = Nothing
                    ElseIf i = _period Then
                        atr = trList.Skip(1).Take(_period).Average()
                        result.IndicatorValue = atr
                    Else
                        atr = ((atr * (_period - 1)) + tr) / _period
                        result.IndicatorValue = atr
                    End If
                End If

                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

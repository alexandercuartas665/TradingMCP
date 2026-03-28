Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Absolute Strength Histogram (ASH)
    ''' Mide el poder intrínseco de los toros (Bulls) versus los osos (Bears).
    ''' </summary>
    Public Class AbsoluteStrengthHistogram
        Implements IIndicator

        Private ReadOnly _period As Integer

        Public Sub New(Optional period As Integer = 9)
            _period = period
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("ASH({0})", _period)
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return "Absolute Strength Histogram: Fuerza Bull vs Bear basada en promedios móviles y momentum."
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

            Dim bullsList As New List(Of Decimal)()
            Dim bearsList As New List(Of Decimal)()

            For i As Integer = 0 To candleList.Count - 1
                If i = 0 Then
                    bullsList.Add(0D)
                    bearsList.Add(0D)
                Else
                    Dim current = candleList(i)
                    Dim prev = candleList(i - 1)
                    
                    Dim bullPower = current.High - prev.ClosePrice
                    Dim bearPower = prev.ClosePrice - current.Low
                    
                    bullsList.Add(If(bullPower > 0, bullPower, 0D))
                    bearsList.Add(If(bearPower > 0, bearPower, 0D))
                End If
            Next

            For i As Integer = 0 To candleList.Count - 1
                Dim current As CandleModel = candleList(i)
                Dim result As New IndicatorResult() With {
                    .Epoch = current.Epoch,
                    .OpenTime = current.OpenTime,
                    .Label = labelStr
                }

                If i < _period Then
                    result.IndicatorValue = Nothing
                Else
                    Dim bullSma = bullsList.Skip(i - _period + 1).Take(_period).Average()
                    Dim bearSma = bearsList.Skip(i - _period + 1).Take(_period).Average()

                    result.IndicatorValue = bullSma - bearSma
                    result.AdditionalValues.Add("Bulls", bullSma)
                    result.AdditionalValues.Add("Bears", bearSma)
                End If

                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

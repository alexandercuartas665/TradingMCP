Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Donchian Channel
    ''' Desarrollado por Richard Donchian, busca altos y bajos en N períodos.
    ''' </summary>
    Public Class DonchianChannel
        Implements IIndicator

        Private ReadOnly _period As Integer

        Public Sub New(Optional period As Integer = 20)
            _period = period
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("Donchian({0})", _period)
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return String.Format("Canal de Donchian ({0}). Identifica extremos de tendencia.", _period)
            End Get
        End Property

        Public ReadOnly Property Category As String Implements IIndicator.Category
            Get
                Return "Trend"
            End Get
        End Property

        Public Function Calculate(candles As IEnumerable(Of CandleModel)) As List(Of IndicatorResult) Implements IIndicator.Calculate
            Dim candleList As List(Of CandleModel) = candles.ToList()
            Dim results As New List(Of IndicatorResult)(candleList.Count)
            Dim labelStr As String = Me.Name

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
                    Dim upper = window.Max(Function(c) c.High)
                    Dim lower = window.Min(Function(c) c.Low)
                    Dim middle = (upper + lower) / 2D

                    result.IndicatorValue = middle
                    result.AdditionalValues.Add("Upper", upper)
                    result.AdditionalValues.Add("Lower", lower)
                End If

                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

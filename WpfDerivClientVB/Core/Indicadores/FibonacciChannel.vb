Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Fibonacci Channel Automático
    ''' Genera niveles estáticos sobre el High/Low del periodo N.
    ''' Útil para analizar retrocesos matemáticos dinámicos.
    ''' </summary>
    Public Class FibonacciChannel
        Implements IIndicator

        Private ReadOnly _period As Integer

        Public Sub New(Optional period As Integer = 100)
            _period = period
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("FibChannel({0})", _period)
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return "Fibonacci Channel (Canal automático). Niveles matemáticos estáticos ajustados al periodo."
            End Get
        End Property

        Public ReadOnly Property Category As String Implements IIndicator.Category
            Get
                Return "Support/Resistance"
            End Get
        End Property

        Public Function Calculate(candles As IEnumerable(Of CandleModel)) As List(Of IndicatorResult) Implements IIndicator.Calculate
            Dim candleList = candles.ToList()
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
                    Dim diff = upper - lower

                    result.IndicatorValue = lower + (diff * 0.5D) ' Nivel 50%
                    
                    result.AdditionalValues.Add("Level0.0", lower)
                    result.AdditionalValues.Add("Level23.6", lower + (diff * 0.236D))
                    result.AdditionalValues.Add("Level38.2", lower + (diff * 0.382D))
                    result.AdditionalValues.Add("Level61.8", lower + (diff * 0.618D))
                    result.AdditionalValues.Add("Level100.0", upper)
                End If

                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

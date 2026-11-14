Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Commodity Channel Index (CCI)
    ''' Mide el nivel de precios relativo a un precio promedio durante un intervalo de tiempo.
    ''' </summary>
    Public Class CCI
        Implements IIndicator

        Private ReadOnly _period As Integer

        Public Sub New(Optional period As Integer = 20)
            _period = period
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("CCI({0})", _period)
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return "Commodity Channel Index. Mide desvíos del precio frente al promedio estadístico."
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

            Dim tpList As New List(Of Decimal)(candleList.Count)

            For i As Integer = 0 To candleList.Count - 1
                Dim current As CandleModel = candleList(i)
                Dim result As New IndicatorResult() With {
                    .Epoch = current.Epoch,
                    .OpenTime = current.OpenTime,
                    .Label = labelStr
                }

                Dim tp = (current.High + current.Low + current.ClosePrice) / 3D
                tpList.Add(tp)

                If i < _period - 1 Then
                    result.IndicatorValue = Nothing
                Else
                    Dim windowTp = tpList.Skip(i - _period + 1).Take(_period).ToList()
                    Dim smaTp = windowTp.Average()
                    Dim meanDeviation = windowTp.Average(Function(val) Math.Abs(smaTp - val))
                    
                    If meanDeviation = 0D Then
                        result.IndicatorValue = 0D
                    Else
                        result.IndicatorValue = (tp - smaTp) / (0.015D * meanDeviation)
                    End If
                End If

                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

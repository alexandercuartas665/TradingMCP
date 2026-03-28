Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Pivot Points (Standard/Traditional)
    ''' Muestra Pivot, Soporte 1-3, Resistencia 1-3 basado en el alto, bajo, cierre previo.
    ''' Normalmente calculado en Timeframes diarios, adaptado a N periodos aquí.
    ''' </summary>
    Public Class PivotPoints
        Implements IIndicator

        Private ReadOnly _period As Integer

        Public Sub New(Optional period As Integer = 1)
            _period = period ' Por defecto el periodo previo (n-1)
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return "PivotPoints"
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return "Soportes y Resistencias basados en Pivot Points Tradicionales."
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

                If i < _period Then
                    result.IndicatorValue = Nothing
                Else
                    Dim prev As CandleModel = candleList(i - _period)
                    Dim p = (prev.High + prev.Low + prev.ClosePrice) / 3D
                    Dim r1 = (2D * p) - prev.Low
                    Dim s1 = (2D * p) - prev.High
                    Dim r2 = p + (prev.High - prev.Low)
                    Dim s2 = p - (prev.High - prev.Low)

                    result.IndicatorValue = p
                    result.AdditionalValues.Add("R1", r1)
                    result.AdditionalValues.Add("S1", s1)
                    result.AdditionalValues.Add("R2", r2)
                    result.AdditionalValues.Add("S2", s2)
                End If

                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

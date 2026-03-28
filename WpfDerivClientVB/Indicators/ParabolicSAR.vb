Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Parabolic Stop and Reverse (SAR)
    ''' Muestra puntos bajo o sobre el precio para marcar dirección e inversiones de tendencia.
    ''' </summary>
    Public Class ParabolicSAR
        Implements IIndicator

        Private ReadOnly _step As Decimal
        Private ReadOnly _maxStep As Decimal

        Public Sub New(Optional stepValue As Decimal = 0.02D, Optional maxStep As Decimal = 0.2D)
            _step = stepValue
            _maxStep = maxStep
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("PSAR({0},{1})", _step, _maxStep)
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return "Parabolic SAR. Puntos que indican la madurez de la tendencia."
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

            Dim isUpTrend As Boolean = True
            Dim sar As Decimal = 0
            Dim ep As Decimal = 0 ' Extreme point
            Dim af As Decimal = _step ' Acceleration factor

            For i As Integer = 0 To candleList.Count - 1
                Dim current As CandleModel = candleList(i)
                Dim result As New IndicatorResult() With {
                    .Epoch = current.Epoch,
                    .OpenTime = current.OpenTime,
                    .Label = labelStr
                }

                If i = 0 Then
                    result.IndicatorValue = Nothing
                    isUpTrend = True
                    sar = current.Low
                    ep = current.High
                    results.Add(result)
                    Continue For
                End If

                Dim prev = candleList(i - 1)

                sar = sar + af * (ep - sar)

                If isUpTrend Then
                    If current.Low < sar Then
                        isUpTrend = False
                        sar = ep
                        ep = current.Low
                        af = _step
                    Else
                        If current.High > ep Then
                            ep = current.High
                            af = Math.Min(af + _step, _maxStep)
                        End If
                        Dim prevPrev = If(i > 1, candleList(i - 2), prev)
                        sar = Math.Min(sar, prev.Low)
                        sar = Math.Min(sar, prevPrev.Low)
                    End If
                Else
                    If current.High > sar Then
                        isUpTrend = True
                        sar = ep
                        ep = current.High
                        af = _step
                    Else
                        If current.Low < ep Then
                            ep = current.Low
                            af = Math.Min(af + _step, _maxStep)
                        End If
                        Dim prevPrev = If(i > 1, candleList(i - 2), prev)
                        sar = Math.Max(sar, prev.High)
                        sar = Math.Max(sar, prevPrev.High)
                    End If
                End If

                result.IndicatorValue = sar
                result.AdditionalValues.Add("Trend", If(isUpTrend, 1D, -1D))
                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

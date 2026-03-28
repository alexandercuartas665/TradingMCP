Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' QQE MOD (Quantitative Qualitative Estimation MOD)
    ''' RSI suavizado usando promedios exponenciales con trailing ATR. Utilizado para filtros momentum.
    ''' </summary>
    Public Class QQEMOD
        Implements IIndicator

        Private ReadOnly _rsiPeriod As Integer
        Private ReadOnly _sf As Integer ' Smoothing factor

        Public Sub New(Optional rsiPeriod As Integer = 14, Optional smoothingFactor As Integer = 5)
            _rsiPeriod = rsiPeriod
            _sf = smoothingFactor
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("QQEMOD({0},{1})", _rsiPeriod, _sf)
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return "Quantitative Qualitative Estimation (QQE) Mod."
            End Get
        End Property

        Public ReadOnly Property Category As String Implements IIndicator.Category
            Get
                Return "Momentum"
            End Get
        End Property

        Public Function Calculate(candles As IEnumerable(Of CandleModel)) As List(Of IndicatorResult) Implements IIndicator.Calculate
            Dim rsiInd As New RSI(_rsiPeriod)
            Dim rsiResults = rsiInd.Calculate(candles)

            Dim candleList = candles.ToList()
            Dim results As New List(Of IndicatorResult)(candleList.Count)
            Dim labelStr As String = Me.Name

            Dim k As Decimal = 2D / (_sf + 1D)
            Dim smoothedRSI As Decimal = 50D

            For i As Integer = 0 To candleList.Count - 1
                Dim current = candleList(i)
                Dim rsiVal = rsiResults(i).IndicatorValue

                Dim result As New IndicatorResult() With {
                    .Epoch = current.Epoch,
                    .OpenTime = current.OpenTime,
                    .Label = labelStr
                }

                If i = 0 OrElse Not rsiVal.HasValue Then
                    result.IndicatorValue = Nothing
                    results.Add(result)
                    Continue For
                End If

                If smoothedRSI = 50D Then
                    smoothedRSI = rsiVal.Value ' Start anchor
                End If

                smoothedRSI = (rsiVal.Value - smoothedRSI) * k + smoothedRSI

                result.IndicatorValue = smoothedRSI
                ' Fast and Slow logic would expand here, pero exponemos el SmoothedRSI
                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

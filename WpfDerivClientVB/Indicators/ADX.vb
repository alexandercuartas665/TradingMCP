Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Average Directional Index (ADX)
    ''' Mide la fortaleza de una tendencia, sin importar su dirección.
    ''' </summary>
    Public Class ADX
        Implements IIndicator

        Private ReadOnly _period As Integer

        Public Sub New(Optional period As Integer = 14)
            _period = period
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("ADX({0})", _period)
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return "Indice de Dirección Promedio. Mide la fuerza intrínseca de una tendencia > 25 indica tendencia fuerte."
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

            Dim trList As New List(Of Decimal)()
            Dim pDMList As New List(Of Decimal)()
            Dim mDMList As New List(Of Decimal)()

            Dim smoothedTR As Decimal = 0
            Dim smoothedPDM As Decimal = 0
            Dim smoothedMDM As Decimal = 0
            Dim dxList As New List(Of Decimal)()

            Dim adxVal As Decimal = 0

            For i As Integer = 0 To candleList.Count - 1
                Dim current As CandleModel = candleList(i)
                Dim result As New IndicatorResult() With {
                    .Epoch = current.Epoch,
                    .OpenTime = current.OpenTime,
                    .Label = labelStr
                }

                If i = 0 Then
                    result.IndicatorValue = Nothing
                    results.Add(result)
                    Continue For
                End If

                Dim prev = candleList(i - 1)
                Dim highLow = current.High - current.Low
                Dim highClose = Math.Abs(current.High - prev.ClosePrice)
                Dim lowClose = Math.Abs(current.Low - prev.ClosePrice)
                Dim tr = Math.Max(highLow, Math.Max(highClose, lowClose))

                Dim upMove = current.High - prev.High
                Dim downMove = prev.Low - current.Low

                Dim pDM = If(upMove > downMove AndAlso upMove > 0, upMove, 0D)
                Dim mDM = If(downMove > upMove AndAlso downMove > 0, downMove, 0D)

                If i < _period Then
                    trList.Add(tr)
                    pDMList.Add(pDM)
                    mDMList.Add(mDM)
                    result.IndicatorValue = Nothing
                ElseIf i = _period Then
                    trList.Add(tr)
                    pDMList.Add(pDM)
                    mDMList.Add(mDM)

                    smoothedTR = trList.Sum()
                    smoothedPDM = pDMList.Sum()
                    smoothedMDM = mDMList.Sum()
                    result.IndicatorValue = Nothing
                    
                    Dim pDI = 100D * (smoothedPDM / smoothedTR)
                    Dim mDI = 100D * (smoothedMDM / smoothedTR)
                    Dim dx = 100D * Math.Abs((pDI - mDI) / (pDI + mDI + 0.00001D))
                    dxList.Add(dx)
                Else
                    smoothedTR = smoothedTR - (smoothedTR / _period) + tr
                    smoothedPDM = smoothedPDM - (smoothedPDM / _period) + pDM
                    smoothedMDM = smoothedMDM - (smoothedMDM / _period) + mDM

                    Dim pDI = 100D * (smoothedPDM / smoothedTR)
                    Dim mDI = 100D * (smoothedMDM / smoothedTR)
                    Dim dx = 100D * Math.Abs((pDI - mDI) / (pDI + mDI + 0.00001D))

                    dxList.Add(dx)

                    Dim adxPeriod = i - _period
                    If adxPeriod < _period Then
                        result.IndicatorValue = Nothing
                    ElseIf adxPeriod = _period Then
                        adxVal = dxList.Skip(dxList.Count - _period).Sum() / _period
                        result.IndicatorValue = adxVal
                        result.AdditionalValues.Add("+DI", pDI)
                        result.AdditionalValues.Add("-DI", mDI)
                    Else
                        adxVal = ((adxVal * (_period - 1)) + dx) / _period
                        result.IndicatorValue = adxVal
                        result.AdditionalValues.Add("+DI", pDI)
                        result.AdditionalValues.Add("-DI", mDI)
                    End If
                End If
                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

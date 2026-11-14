Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Money Flow Index (MFI)
    ''' Normalizado para volumen aproximado (MFI es un RSI sobre el precio típico * volumen).
    ''' En ausencia de volumen real en CandleModel, usa una aproximación de volumen basada en el rango High-Low.
    ''' </summary>
    Public Class MFI
        Implements IIndicator

        Private ReadOnly _period As Integer

        Public Sub New(Optional period As Integer = 14)
            _period = period
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("MFI({0})", _period)
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return "Money Flow Index. Oscilador >80 indica sobrecompra, <20 sobreventa."
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

            Dim tpList As New List(Of Decimal)
            Dim rmList As New List(Of Decimal)

            For i As Integer = 0 To candleList.Count - 1
                Dim current As CandleModel = candleList(i)
                Dim tp = (current.High + current.Low + current.ClosePrice) / 3D
                Dim approxVolume = If(current.High = current.Low, 1D, current.High - current.Low)
                Dim rm = tp * approxVolume

                tpList.Add(tp)
                rmList.Add(rm)
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
                    Dim posMF As Decimal = 0
                    Dim negMF As Decimal = 0

                    For j As Integer = i - _period + 1 To i
                        If tpList(j) > tpList(j - 1) Then
                            posMF += rmList(j)
                        ElseIf tpList(j) < tpList(j - 1) Then
                            negMF += rmList(j)
                        End If
                    Next

                    If negMF = 0 Then
                        result.IndicatorValue = 100D
                    Else
                        Dim mfr = posMF / negMF
                        result.IndicatorValue = 100D - (100D / (1D + mfr))
                    End If
                End If

                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

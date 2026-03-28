Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Ichimoku Kinko Hyo
    ''' Sistema de tendencia integral. Mide momento y áreas futuras de soporte/resistencia.
    ''' </summary>
    Public Class Ichimoku
        Implements IIndicator

        Private ReadOnly _tenkan As Integer
        Private ReadOnly _kijun As Integer
        Private ReadOnly _senkouB As Integer

        Public Sub New(Optional tenkan As Integer = 9, Optional kijun As Integer = 26, Optional senkouB As Integer = 52)
            _tenkan = tenkan
            _kijun = kijun
            _senkouB = senkouB
        End Sub

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("Ichimoku({0},{1},{2})", _tenkan, _kijun, _senkouB)
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return "Ichimoku Cloud. Líneas Tenkan, Kijun, Senkou Span A y B."
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

            For i As Integer = 0 To candleList.Count - 1
                Dim current As CandleModel = candleList(i)
                Dim result As New IndicatorResult() With {
                    .Epoch = current.Epoch,
                    .OpenTime = current.OpenTime,
                    .Label = labelStr
                }

                Dim tenkanVal As Nullable(Of Decimal) = Nothing
                Dim kijunVal  As Nullable(Of Decimal) = Nothing
                Dim sSpanBVal As Nullable(Of Decimal) = Nothing

                If i >= _tenkan - 1 Then
                    Dim w = candleList.Skip(i - _tenkan + 1).Take(_tenkan)
                    tenkanVal = (w.Max(Function(c) c.High) + w.Min(Function(c) c.Low)) / 2D
                End If

                If i >= _kijun - 1 Then
                    Dim w = candleList.Skip(i - _kijun + 1).Take(_kijun)
                    kijunVal = (w.Max(Function(c) c.High) + w.Min(Function(c) c.Low)) / 2D
                End If

                If i >= _senkouB - 1 Then
                    Dim w = candleList.Skip(i - _senkouB + 1).Take(_senkouB)
                    sSpanBVal = (w.Max(Function(c) c.High) + w.Min(Function(c) c.Low)) / 2D
                End If

                result.IndicatorValue = tenkanVal
                
                If kijunVal.HasValue Then result.AdditionalValues.Add("Kijun", kijunVal.Value)
                
                If tenkanVal.HasValue AndAlso kijunVal.HasValue Then
                    result.AdditionalValues.Add("SenkouSpanA", (tenkanVal.Value + kijunVal.Value) / 2D)
                End If

                If sSpanBVal.HasValue Then result.AdditionalValues.Add("SenkouSpanB", sSpanBVal.Value)

                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

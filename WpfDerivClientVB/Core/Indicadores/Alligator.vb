Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Bill Williams Alligator
    ''' Jaw, Teeth, y Lips basados en variables suavizadas y desfasadas para indicar tendencias.
    ''' </summary>
    Public Class Alligator
        Implements IIndicator

        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return "Alligator(13,8,5)"
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return "Alligator (Bill Williams): Jaw, Teeth y Lips mostrando tendencias superpuestas."
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

            Dim mediPriceList As New List(Of Decimal)()
            For i As Integer = 0 To candleList.Count - 1
                mediPriceList.Add((candleList(i).High + candleList(i).Low) / 2)
            Next

            For i As Integer = 0 To candleList.Count - 1
                Dim current As CandleModel = candleList(i)
                Dim result As New IndicatorResult() With {
                    .Epoch = current.Epoch,
                    .OpenTime = current.OpenTime,
                    .Label = labelStr
                }

                result.IndicatorValue = Nothing ' No single value

                Dim jawIdx = i - 8
                If jawIdx >= 13 Then
                    Dim jawSma = mediPriceList.Skip(jawIdx - 13 + 1).Take(13).Average()
                    result.AdditionalValues.Add("Jaw", jawSma)
                End If

                Dim teethIdx = i - 5
                If teethIdx >= 8 Then
                    Dim teethSma = mediPriceList.Skip(teethIdx - 8 + 1).Take(8).Average()
                    result.AdditionalValues.Add("Teeth", teethSma)
                End If

                Dim lipsIdx = i - 3
                If lipsIdx >= 5 Then
                    Dim lipsSma = mediPriceList.Skip(lipsIdx - 5 + 1).Take(5).Average()
                    result.AdditionalValues.Add("Lips", lipsSma)
                End If

                results.Add(result)
            Next

            Return results
        End Function
    End Class

End Namespace

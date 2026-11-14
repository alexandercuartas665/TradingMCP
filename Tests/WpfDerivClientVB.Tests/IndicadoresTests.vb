Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports WpfDerivClientVB.WpfDerivClientVB
Imports WpfDerivClientVB.WpfDerivClientVB.Indicators

''' <summary>Propiedades basicas del RSI de Wilder.</summary>
<TestClass>
Public Class IndicadoresTests

    Private Shared Function Velas(cierres As IEnumerable(Of Decimal)) As List(Of CandleModel)
        Dim lista As New List(Of CandleModel)()
        Dim epoch As Long = 1700000000
        For Each c In cierres
            lista.Add(New CandleModel With {.Epoch = epoch, .OpenPrice = c, .High = c, .Low = c, .ClosePrice = c})
            epoch += 60
        Next
        Return lista
    End Function

    <TestMethod>
    Public Sub Los_primeros_periodos_no_tienen_valor()
        Dim r = New RSI(14).Calculate(Velas(Enumerable.Range(1, 30).Select(Function(i) CDec(i))))
        Assert.AreEqual(30, r.Count)
        For i = 0 To 13
            Assert.IsFalse(r(i).IndicatorValue.HasValue, "indice " & i)
        Next
        Assert.IsTrue(r(29).IndicatorValue.HasValue)
    End Sub

    <TestMethod>
    Public Sub Serie_siempre_al_alza_da_RSI_100()
        Dim r = New RSI(14).Calculate(Velas(Enumerable.Range(1, 40).Select(Function(i) CDec(i))))
        Assert.AreEqual(100D, r.Last().IndicatorValue.Value)
    End Sub

    <TestMethod>
    Public Sub Serie_siempre_a_la_baja_da_RSI_0()
        Dim r = New RSI(14).Calculate(Velas(Enumerable.Range(1, 40).Select(Function(i) CDec(100 - i))))
        Assert.AreEqual(0D, r.Last().IndicatorValue.Value)
    End Sub

    <TestMethod>
    Public Sub RSI_siempre_entre_0_y_100()
        Dim rnd As New Random(75)
        Dim precio = 1250D
        Dim cierres As New List(Of Decimal)()
        For i = 1 To 500
            precio += CDec(rnd.NextDouble() - 0.5) * 5D
            cierres.Add(precio)
        Next
        For Each x In New RSI(14).Calculate(Velas(cierres)).Where(Function(v) v.IndicatorValue.HasValue)
            Assert.IsTrue(x.IndicatorValue.Value >= 0D AndAlso x.IndicatorValue.Value <= 100D)
        Next
    End Sub

End Class

Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas

''' <summary>
''' La duracion del contrato en Deriv debe ser la misma que mide el backtest:
''' N velas del timeframe. Antes se enviaba N en ticks.
''' </summary>
<TestClass>
Public Class DuracionDerivTests

    Private Shared Function Convertir(tf As String, velas As Integer, ByRef unidad As String) As Integer
        Return LiveTestMotor.DuracionDeriv(tf, velas, unidad)
    End Function

    <DataTestMethod>
    <DataRow("1m", 5, 5, "m")>
    <DataRow("5m", 3, 15, "m")>
    <DataRow("15m", 2, 30, "m")>
    <DataRow("30m", 4, 120, "m")>
    <DataRow("1h", 2, 120, "m")>
    <DataRow("4h", 2, 480, "m")>
    <DataRow("4h", 6, 1440, "m")>
    <DataRow("1d", 2, 2, "d")>
    <DataRow("1H", 1, 60, "m")>
    Public Sub Convierte_velas_a_minutos_o_dias(tf As String, velas As Integer, esperado As Integer, unidadEsperada As String)
        Dim unidad As String = ""
        Assert.AreEqual(esperado, Convertir(tf, velas, unidad))
        Assert.AreEqual(unidadEsperada, unidad)
    End Sub

    <DataTestMethod>
    <DataRow("4h", 7)>     ' 28 h: no cabe en minutos (max 1440) ni es multiplo de dia
    <DataRow("1h", 30)>
    <DataRow("xx", 2)>
    <DataRow("", 2)>
    <DataRow("1h", 0)>
    <DataRow("1d", 400)>   ' mas de 365 dias
    Public Sub Combinaciones_no_soportadas_devuelven_cero(tf As String, velas As Integer)
        Dim unidad As String = "?"
        Assert.AreEqual(0, Convertir(tf, velas, unidad))
        Assert.AreEqual("", unidad)
    End Sub

End Class

Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Servidor

''' <summary>Reglas del modo autonomo: cuando avisar, cuando mantener y que copias borrar.</summary>
<TestClass>
Public Class ModoAutonomoTests

    Private Shared Function Sub1h(minutos As Double, Optional abierto As Boolean = True, Optional inicializada As Boolean = True) As JObject
        Return New JObject From {{"granularidad", 3600}, {"inicializada", inicializada}, {"mercado_abierto", abierto},
                                 {"minutos_desde_ultima_vela", minutos}}
    End Function

    <TestMethod>
    Public Sub Avisa_tras_2_horas_sin_velas_con_el_mercado_abierto()
        Assert.IsFalse(ModoAutonomo.VelasAtrasadas(Sub1h(90)))
        Assert.IsFalse(ModoAutonomo.VelasAtrasadas(Sub1h(125)))   ' limite 1h: 2 velas + 10 min = 130
        Assert.IsTrue(ModoAutonomo.VelasAtrasadas(Sub1h(135)))
    End Sub

    <TestMethod>
    Public Sub No_avisa_con_el_mercado_cerrado_ni_antes_de_la_primera_consulta()
        Assert.IsFalse(ModoAutonomo.VelasAtrasadas(Sub1h(600, abierto:=False)))
        Assert.IsFalse(ModoAutonomo.VelasAtrasadas(Sub1h(600, inicializada:=False)))
    End Sub

    <TestMethod>
    Public Sub En_4h_espera_dos_velas_mas_margen()
        Dim e = New JObject From {{"granularidad", 14400}, {"inicializada", True}, {"mercado_abierto", True}, {"minutos_desde_ultima_vela", 300}}
        Assert.IsFalse(ModoAutonomo.VelasAtrasadas(e))     ' limite 490 min
        e("minutos_desde_ultima_vela") = 500
        Assert.IsTrue(ModoAutonomo.VelasAtrasadas(e))
    End Sub

    <TestMethod>
    Public Sub Mantenimiento_una_vez_al_dia_despues_de_las_22_10_UTC()
        Dim hoy = New DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc)
        Assert.IsFalse(ModoAutonomo.TocaMantenimiento(hoy.AddHours(22).AddMinutes(5), Date.MinValue))
        Assert.IsTrue(ModoAutonomo.TocaMantenimiento(hoy.AddHours(22).AddMinutes(10), Date.MinValue))
        Assert.IsFalse(ModoAutonomo.TocaMantenimiento(hoy.AddHours(23), hoy.Date))          ' ya se hizo hoy
        Assert.IsTrue(ModoAutonomo.TocaMantenimiento(hoy.AddDays(1).AddHours(22).AddMinutes(30), hoy.Date))
    End Sub

    <TestMethod>
    Public Sub Conserva_las_copias_mas_recientes()
        Dim nombres = {"traiding_datos_20261110_2210.dump", "traiding_datos_20261112_2210.dump",
                       "traiding_datos_20261111_2210.dump", "traiding_datos_20261113_2210.dump"}
        CollectionAssert.AreEqual({"traiding_datos_20261111_2210.dump", "traiding_datos_20261110_2210.dump"},
                                  ModoAutonomo.RespaldosABorrar(nombres, 2).ToArray())
        Assert.AreEqual(0, ModoAutonomo.RespaldosABorrar(nombres, 14).Count)
    End Sub

End Class

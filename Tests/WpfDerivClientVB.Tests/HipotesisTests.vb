Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting

''' <summary>Correccion por pruebas multiples del embudo de hipotesis.</summary>
<TestClass>
Public Class HipotesisTests

    <TestMethod>
    Public Sub Z_de_la_normal_coincide_con_las_tablas()
        Assert.AreEqual(1.96, Estadistica.ZDeCola(0.025), 0.001)
        Assert.AreEqual(1.645, Estadistica.ZDeCola(0.05), 0.001)
        Assert.AreEqual(2.807, Estadistica.ZDeCola(0.0025), 0.001)    ' 95% repartido entre 10 pruebas
        Assert.AreEqual(3.481, Estadistica.ZDeCola(0.00025), 0.001)   ' entre 100 pruebas
    End Sub

    <TestMethod>
    Public Sub Con_una_sola_prueba_es_el_veredicto_normal()
        For Each caso In {Tuple.Create(70, 120), Tuple.Create(60, 100), Tuple.Create(48, 100), Tuple.Create(10, 20)}
            Assert.AreEqual(Estadistica.Veredicto(caso.Item1, caso.Item2, 85D),
                            Estadistica.VeredictoCorregido(caso.Item1, caso.Item2, 85D, 1))
        Next
    End Sub

    <TestMethod>
    Public Sub Una_ventaja_justa_no_sobrevive_a_muchas_pruebas()
        ' 64% en 120 operaciones con minimo 54,1%: ventaja con 1 prueba, no con 50
        Assert.AreEqual("VENTAJA ESTADISTICA", Estadistica.VeredictoCorregido(77, 120, 85D, 1))
        Assert.AreEqual("POSITIVO, NO SIGNIFICATIVO", Estadistica.VeredictoCorregido(77, 120, 85D, 50))
        ' Una ventaja muy clara si sobrevive
        Assert.AreEqual("VENTAJA ESTADISTICA", Estadistica.VeredictoCorregido(300, 400, 85D, 50))
    End Sub

    <TestMethod>
    Public Sub Candidata_baja_a_prometedora_si_no_sobrevive_la_correccion()
        Dim c = "CANDIDATA: ventaja en el periodo ciego. Siguiente paso: forward test en demo con las reglas fijadas de antemano."
        Assert.IsTrue(ValidacionCompleta.ConclusionCorregida(c, "POSITIVO, NO SIGNIFICATIVO", 12).StartsWith("PROMETEDORA"))
        Assert.AreEqual(c, ValidacionCompleta.ConclusionCorregida(c, "VENTAJA ESTADISTICA", 12))
        Assert.AreEqual("SIN VENTAJA DEMOSTRADA: no operar con dinero real.",
                        ValidacionCompleta.ConclusionCorregida("SIN VENTAJA DEMOSTRADA: no operar con dinero real.", "SIN VENTAJA", 3))
    End Sub

End Class

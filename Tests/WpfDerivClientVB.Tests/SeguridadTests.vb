Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports WpfDerivClientVB.WpfDerivClientVB

''' <summary>Cifrado de tokens (DPAPI) y comparacion en tiempo constante.</summary>
<TestClass>
Public Class SeguridadTests

    <TestMethod>
    Public Sub Token_cifrado_se_descifra_igual_y_no_queda_en_claro()
        Dim token = "pat_ejemplo_0123456789abcdef0123456789abcdef"
        Dim guardado = SecretProtector.Protect(token)
        Assert.IsTrue(guardado.StartsWith("dpapi:"))
        Assert.IsFalse(guardado.Contains(token))
        Assert.AreEqual(token, SecretProtector.Unprotect(guardado))
    End Sub

    <TestMethod>
    Public Sub Valor_antiguo_en_texto_plano_se_devuelve_tal_cual()
        Assert.AreEqual("pat_viejo", SecretProtector.Unprotect("pat_viejo"))
    End Sub

    <TestMethod>
    Public Sub Vacio_se_mantiene_vacio_y_no_se_cifra_dos_veces()
        Assert.AreEqual("", SecretProtector.Protect(""))
        Assert.AreEqual("", SecretProtector.Unprotect(Nothing))
        Dim una = SecretProtector.Protect("x")
        Assert.AreEqual(una, SecretProtector.Protect(una))
    End Sub

    <TestMethod>
    Public Sub Blob_alterado_devuelve_vacio_sin_lanzar()
        Assert.AreEqual("", SecretProtector.Unprotect("dpapi:AAAAAAAAAAAA"))
    End Sub

    <TestMethod>
    Public Sub Comparacion_en_tiempo_constante()
        Assert.IsTrue(AgentAuth.IgualTiempoConstante("abc123", "abc123"))
        Assert.IsFalse(AgentAuth.IgualTiempoConstante("abc123", "abc124"))
        Assert.IsFalse(AgentAuth.IgualTiempoConstante("abc", "abc123"))
        Assert.IsFalse(AgentAuth.IgualTiempoConstante(Nothing, "abc"))
    End Sub

End Class

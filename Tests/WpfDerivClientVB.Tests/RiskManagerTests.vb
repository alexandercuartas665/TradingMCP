Imports System.IO
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports WpfDerivClientVB.WpfDerivClientVB

''' <summary>
''' Reglas del gestor de riesgo con los limites por defecto
''' (sin db_config.json en la carpeta de pruebas): monto max 2, perdida diaria 10,
''' 3 contratos abiertos, operacion automatica en REAL bloqueada.
''' Cada prueba usa un archivo de estado temporal propio.
''' </summary>
<TestClass>
<DoNotParallelize>
Public Class RiskManagerTests

    Private _ruta As String

    <TestInitialize>
    Public Sub Preparar()
        _ruta = Path.Combine(Path.GetTempPath(), "riesgo_test_" & Guid.NewGuid().ToString("N") & ".json")
        RiskManager.RutaEstadoOverride = _ruta
        RiskManager.ReiniciarParaPruebas()
    End Sub

    <TestCleanup>
    Public Sub Limpiar()
        RiskManager.ReiniciarParaPruebas()
        RiskManager.RutaEstadoOverride = Nothing
        If File.Exists(_ruta) Then File.Delete(_ruta)
    End Sub

    Private Shared Sub PerderContrato(id As String, monto As Decimal)
        RiskManager.RegistrarApertura(id, monto)
        RiskManager.RegistrarCierre(id, -monto)
    End Sub

    <TestMethod>
    Public Sub Permite_operacion_valida_en_demo()
        Assert.AreEqual("", RiskManager.ValidarYReservar(1D, True))
        RiskManager.LiberarReserva()
    End Sub

    <TestMethod>
    Public Sub Bloquea_monto_mayor_al_maximo()
        StringAssert.Contains(RiskManager.ValidarYReservar(5D, True), "supera el maximo")
    End Sub

    <TestMethod>
    Public Sub Bloquea_operacion_automatica_en_cuenta_real()
        StringAssert.Contains(RiskManager.ValidarYReservar(1D, False), "REAL")
    End Sub

    <TestMethod>
    Public Sub Cuenta_compras_en_curso_para_el_maximo_de_contratos()
        For i = 1 To 3
            Assert.AreEqual("", RiskManager.ValidarYReservar(1D, True))
        Next
        StringAssert.Contains(RiskManager.ValidarYReservar(1D, True), "contratos abiertos")
        RiskManager.LiberarReserva()
        Assert.AreEqual("", RiskManager.ValidarYReservar(1D, True), "Al liberar una reserva debe quedar cupo")
    End Sub

    <TestMethod>
    Public Sub Contrato_informado_dos_veces_cuenta_una_sola_vez()
        PerderContrato("c1", 1D)
        RiskManager.RegistrarCierre("c1", -1D)
        Assert.AreEqual(-1D, RiskManager.GetEstado()("pnl_dia").ToObject(Of Decimal)())
    End Sub

    <TestMethod>
    Public Sub Bloquea_si_perder_la_nueva_operacion_superaria_el_limite_diario()
        For i = 1 To 9
            PerderContrato("c" & i, 1D)
        Next
        StringAssert.Contains(RiskManager.ValidarYReservar(1.5D, True), "superaria la perdida maxima")
        Assert.AreEqual("", RiskManager.ValidarYReservar(1D, True), "Con -9 y limite -10, una apuesta de 1 aun cabe")
        RiskManager.LiberarReserva()
    End Sub

    <TestMethod>
    Public Sub Activa_parada_global_al_alcanzar_la_perdida_maxima()
        For i = 1 To 10
            PerderContrato("c" & i, 1D)
        Next
        Assert.IsTrue(RiskManager.ParadaActiva)
        StringAssert.Contains(RiskManager.ValidarYReservar(1D, True), "Parada global")
    End Sub

    <TestMethod>
    Public Sub Quitar_la_parada_no_salta_el_limite_diario()
        For i = 1 To 10
            PerderContrato("c" & i, 1D)
        Next
        RiskManager.DesactivarParada()
        StringAssert.Contains(RiskManager.ValidarYReservar(1D, True), "Perdida maxima diaria alcanzada")
    End Sub

    <TestMethod>
    Public Sub El_estado_del_dia_sobrevive_a_un_reinicio()
        PerderContrato("c1", 1D)
        PerderContrato("c2", 1D)
        RiskManager.ActivarParada("prueba")
        RiskManager.ReiniciarParaPruebas()   ' simula reiniciar la app: relee el archivo
        Dim e = RiskManager.GetEstado()
        Assert.AreEqual(-2D, e("pnl_dia").ToObject(Of Decimal)())
        Assert.IsTrue(e("parada_activa").ToObject(Of Boolean)())
    End Sub

    <TestMethod>
    Public Sub Cierres_de_dias_anteriores_no_cuentan_para_hoy()
        Dim ayer = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds()
        RiskManager.RegistrarCierre("viejo", -50D, ayer)
        Assert.AreEqual(0D, RiskManager.GetEstado()("pnl_dia").ToObject(Of Decimal)())
        Assert.IsFalse(RiskManager.ParadaActiva)
    End Sub

    <TestMethod>
    Public Sub Parada_manual_bloquea_y_se_puede_quitar()
        RiskManager.ActivarParada("emergencia")
        StringAssert.Contains(RiskManager.ValidarYReservar(1D, True), "emergencia")
        RiskManager.DesactivarParada()
        Assert.AreEqual("", RiskManager.ValidarYReservar(1D, True))
        RiskManager.LiberarReserva()
    End Sub

End Class

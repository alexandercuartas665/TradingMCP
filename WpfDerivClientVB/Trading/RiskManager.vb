' ============================================================
' RiskManager.vb
'
' Gestor de riesgo UNICO para todas las operaciones automaticas
' (estrategias live, API HTTP, instrucciones SignalR). Se consulta
' en MainWindow.ApiExecuteTrade ANTES de enviar nada a Deriv.
'
' Reglas (configurables en db_config.json):
'   RiesgoMontoMaximo          tope de stake por operacion   (def. 2)
'   RiesgoPerdidaMaximaDiaria  perdida maxima del dia UTC    (def. 10)
'   RiesgoMaxContratosAbiertos contratos abiertos a la vez   (def. 3)
'   PermitirRealAutomatico     operar en cuenta REAL         (def. False)
'
' Kill switch (parada global): bloquea toda operacion automatica.
' Se activa a mano (API / SignalR firmado) o automaticamente al
' alcanzar la perdida maxima diaria. Solo se desactiva en local.
'
' El PnL del dia sale de los resultados REALES de Deriv
' (proposal_open_contract al cerrarse cada contrato), no de la
' simulacion de los motores. El estado del dia se persiste en
' %LOCALAPPDATA%\WpfDerivClientVB\riesgo_estado.json para que un
' reinicio de la app no borre la perdida acumulada.
' ============================================================
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB

    Public Module RiskManager

        Private ReadOnly _lock As New Object()

        ' --- estado persistido ---
        Private _fechaUtc As String = ""                 ' yyyy-MM-dd del dia en curso
        Private _pnlDia As Decimal = 0D
        Private _operacionesDia As Integer = 0
        Private _killSwitch As Boolean = False
        Private _motivoKill As String = ""
        Private _cerradosDia As New HashSet(Of String)()  ' contract_id ya contabilizados hoy

        ' --- estado en memoria ---
        Private ReadOnly _abiertos As New Dictionary(Of String, Decimal)()  ' contract_id -> stake
        Private _reservas As Integer = 0                  ' compras en curso (aun sin contract_id)
        Private _cargado As Boolean = False

        ''' <summary>Solo pruebas: ruta alternativa del archivo de estado.</summary>
        Friend Property RutaEstadoOverride As String = Nothing

        Private ReadOnly Property RutaEstado As String
            Get
                If Not String.IsNullOrEmpty(RutaEstadoOverride) Then Return RutaEstadoOverride
                Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "WpfDerivClientVB", "riesgo_estado.json")
            End Get
        End Property

        ''' <summary>Solo pruebas: olvida el estado en memoria (el proximo acceso relee el archivo).</summary>
        Friend Sub ReiniciarParaPruebas()
            SyncLock _lock
                _cargado = False
                _fechaUtc = ""
                _pnlDia = 0D
                _operacionesDia = 0
                _killSwitch = False
                _motivoKill = ""
                _cerradosDia.Clear()
                _abiertos.Clear()
                _reservas = 0
            End SyncLock
        End Sub

        ' ── Validacion ──────────────────────────────────────────

        ''' <summary>
        ''' Valida una operacion automatica y, si pasa, reserva un cupo de contrato abierto.
        ''' Devuelve "" si se permite (y entonces hay que llamar LiberarReserva al terminar)
        ''' o el motivo del rechazo (sin reserva).
        ''' </summary>
        Public Function ValidarYReservar(monto As Decimal, esDemo As Boolean) As String
            Dim cfg = ConfigManager.LoadDbConfig()
            SyncLock _lock
                AsegurarDia()
                Dim motivo As String = ""
                If _killSwitch Then
                    motivo = "Parada global activa: " & _motivoKill
                ElseIf Not esDemo AndAlso Not cfg.PermitirRealAutomatico Then
                    motivo = "Operacion automatica en cuenta REAL bloqueada (PermitirRealAutomatico=false)."
                ElseIf monto <= 0D Then
                    motivo = "Monto invalido."
                ElseIf monto > cfg.RiesgoMontoMaximo Then
                    motivo = String.Format(Globalization.CultureInfo.InvariantCulture,
                                           "Monto {0:F2} supera el maximo por operacion ({1:F2}).", monto, cfg.RiesgoMontoMaximo)
                ElseIf cfg.RiesgoPerdidaMaximaDiaria > 0D AndAlso _pnlDia <= -cfg.RiesgoPerdidaMaximaDiaria Then
                    motivo = String.Format(Globalization.CultureInfo.InvariantCulture,
                                           "Perdida maxima diaria alcanzada ({0:F2} de -{1:F2}).", _pnlDia, cfg.RiesgoPerdidaMaximaDiaria)
                ElseIf cfg.RiesgoPerdidaMaximaDiaria > 0D AndAlso _pnlDia - monto < -cfg.RiesgoPerdidaMaximaDiaria Then
                    motivo = String.Format(Globalization.CultureInfo.InvariantCulture,
                                           "Si esta operacion se pierde, se superaria la perdida maxima diaria ({0:F2} - {1:F2} < -{2:F2}).",
                                           _pnlDia, monto, cfg.RiesgoPerdidaMaximaDiaria)
                ElseIf cfg.RiesgoMaxContratosAbiertos > 0 AndAlso _abiertos.Count + _reservas >= cfg.RiesgoMaxContratosAbiertos Then
                    motivo = String.Format("Maximo de contratos abiertos alcanzado ({0} abiertos + {1} en curso, maximo {2}).",
                                           _abiertos.Count, _reservas, cfg.RiesgoMaxContratosAbiertos)
                End If

                If motivo <> "" Then
                    AppLogger.Warn("[Riesgo] Operacion BLOQUEADA: " & motivo)
                    Return motivo
                End If
                _reservas += 1
                Return ""
            End SyncLock
        End Function

        ''' <summary>Libera la reserva hecha por ValidarYReservar (llamar siempre, haya compra o no).</summary>
        Public Sub LiberarReserva()
            SyncLock _lock
                If _reservas > 0 Then _reservas -= 1
            End SyncLock
        End Sub

        ' ── Eventos de Deriv ────────────────────────────────────

        ''' <summary>Llamar cuando Deriv confirma una compra (mensaje "buy").</summary>
        Public Sub RegistrarApertura(contractId As String, monto As Decimal)
            If String.IsNullOrEmpty(contractId) Then Return
            SyncLock _lock
                AsegurarDia()
                If Not _abiertos.ContainsKey(contractId) AndAlso Not _cerradosDia.Contains(contractId) Then
                    _abiertos(contractId) = monto
                    _operacionesDia += 1
                    Guardar()
                End If
            End SyncLock
        End Sub

        ''' <summary>
        ''' Llamar cuando Deriv informa que el contrato termino (won/lost/sold).
        ''' Idempotente: cada contract_id se contabiliza una sola vez por dia.
        ''' </summary>
        Public Sub RegistrarCierre(contractId As String, profit As Decimal, Optional cierreEpochUtc As Long = 0)
            If String.IsNullOrEmpty(contractId) Then Return
            Dim cfg = ConfigManager.LoadDbConfig()
            SyncLock _lock
                AsegurarDia()
                _abiertos.Remove(contractId)
                If _cerradosDia.Contains(contractId) Then Return
                ' Cierres de dias anteriores (p.ej. al reconciliar contratos viejos con Deriv)
                ' no cuentan para la perdida de HOY.
                If cierreEpochUtc > 0 Then
                    Dim fechaCierre = DateTimeOffset.FromUnixTimeSeconds(cierreEpochUtc).UtcDateTime.ToString("yyyy-MM-dd")
                    If fechaCierre <> _fechaUtc Then Return
                End If
                _cerradosDia.Add(contractId)
                _pnlDia += profit
                AppLogger.Info(String.Format(Globalization.CultureInfo.InvariantCulture,
                    "[Riesgo] Contrato {0} cerrado: {1:+0.00;-0.00}. PnL del dia: {2:+0.00;-0.00}", contractId, profit, _pnlDia))

                If cfg.RiesgoPerdidaMaximaDiaria > 0D AndAlso _pnlDia <= -cfg.RiesgoPerdidaMaximaDiaria AndAlso Not _killSwitch Then
                    _killSwitch = True
                    _motivoKill = String.Format(Globalization.CultureInfo.InvariantCulture,
                        "Perdida maxima diaria alcanzada ({0:F2}) el {1:yyyy-MM-dd HH:mm} UTC.", _pnlDia, DateTime.UtcNow)
                    AppLogger.Warn("[Riesgo] PARADA GLOBAL automatica: " & _motivoKill)
                End If
                Guardar()
            End SyncLock
        End Sub

        ' ── Kill switch ─────────────────────────────────────────

        Public Sub ActivarParada(motivo As String)
            SyncLock _lock
                AsegurarDia()
                _killSwitch = True
                _motivoKill = If(String.IsNullOrWhiteSpace(motivo), "Activada manualmente", motivo) &
                              " (" & DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") & " UTC)"
                AppLogger.Warn("[Riesgo] PARADA GLOBAL activada: " & _motivoKill)
                Guardar()
            End SyncLock
        End Sub

        Public Sub DesactivarParada()
            SyncLock _lock
                AsegurarDia()
                _killSwitch = False
                AppLogger.Warn("[Riesgo] Parada global DESACTIVADA (antes: " & _motivoKill & ")")
                _motivoKill = ""
                Guardar()
            End SyncLock
        End Sub

        Public ReadOnly Property ParadaActiva As Boolean
            Get
                SyncLock _lock
                    AsegurarDia()
                    Return _killSwitch
                End SyncLock
            End Get
        End Property

        ''' <summary>Snapshot del estado para la API / UI.</summary>
        Public Function GetEstado() As JObject
            Dim cfg = ConfigManager.LoadDbConfig()
            SyncLock _lock
                AsegurarDia()
                Return New JObject From {
                    {"ok", True},
                    {"fecha_utc", _fechaUtc},
                    {"parada_activa", _killSwitch},
                    {"motivo_parada", _motivoKill},
                    {"pnl_dia", _pnlDia},
                    {"operaciones_dia", _operacionesDia},
                    {"contratos_abiertos", _abiertos.Count},
                    {"compras_en_curso", _reservas},
                    {"limites", New JObject From {
                        {"monto_maximo", cfg.RiesgoMontoMaximo},
                        {"perdida_maxima_diaria", cfg.RiesgoPerdidaMaximaDiaria},
                        {"max_contratos_abiertos", cfg.RiesgoMaxContratosAbiertos},
                        {"permitir_real_automatico", cfg.PermitirRealAutomatico}
                    }}
                }
            End SyncLock
        End Function

        ' ── Persistencia ────────────────────────────────────────

        ''' <summary>Carga el estado la primera vez y lo reinicia al cambiar el dia UTC.</summary>
        Private Sub AsegurarDia()
            If Not _cargado Then
                _cargado = True
                Cargar()
            End If
            Dim hoy = DateTime.UtcNow.ToString("yyyy-MM-dd")
            If _fechaUtc <> hoy Then
                If _fechaUtc <> "" Then
                    AppLogger.Info(String.Format(Globalization.CultureInfo.InvariantCulture,
                        "[Riesgo] Nuevo dia UTC. Cierre de {0}: PnL {1:+0.00;-0.00} en {2} operaciones.", _fechaUtc, _pnlDia, _operacionesDia))
                End If
                ' La parada automatica por perdida diaria se levanta con el nuevo dia;
                ' una parada manual se mantiene hasta desactivarla a mano.
                If _killSwitch AndAlso _motivoKill.StartsWith("Perdida maxima diaria") Then
                    _killSwitch = False
                    _motivoKill = ""
                End If
                _fechaUtc = hoy
                _pnlDia = 0D
                _operacionesDia = 0
                _cerradosDia.Clear()
                Guardar()
            End If
        End Sub

        Private Sub Cargar()
            Try
                If Not File.Exists(RutaEstado) Then Return
                Dim j = JObject.Parse(File.ReadAllText(RutaEstado))
                _fechaUtc = If(j("fecha_utc")?.ToString(), "")
                _pnlDia = If(j("pnl_dia") IsNot Nothing, j("pnl_dia").Value(Of Decimal)(), 0D)
                _operacionesDia = If(j("operaciones_dia") IsNot Nothing, j("operaciones_dia").Value(Of Integer)(), 0)
                _killSwitch = If(j("parada_activa") IsNot Nothing, j("parada_activa").Value(Of Boolean)(), False)
                _motivoKill = If(j("motivo_parada")?.ToString(), "")
                _cerradosDia = New HashSet(Of String)(If(j("cerrados_dia")?.Values(Of String)(), Enumerable.Empty(Of String)()))
            Catch ex As Exception
                ' Ante un archivo danado, ser conservador: activar la parada.
                _killSwitch = True
                _motivoKill = "Estado de riesgo ilegible (" & ex.Message & "). Revisar y desactivar a mano."
                AppLogger.Warn("[Riesgo] " & _motivoKill)
            End Try
        End Sub

        Private Sub Guardar()
            Try
                Directory.CreateDirectory(Path.GetDirectoryName(RutaEstado))
                Dim j As New JObject From {
                    {"fecha_utc", _fechaUtc},
                    {"pnl_dia", _pnlDia},
                    {"operaciones_dia", _operacionesDia},
                    {"parada_activa", _killSwitch},
                    {"motivo_parada", _motivoKill},
                    {"cerrados_dia", New JArray(_cerradosDia.ToArray())}
                }
                Dim tmp = RutaEstado & ".tmp"
                File.WriteAllText(tmp, j.ToString(Formatting.Indented))
                If File.Exists(RutaEstado) Then File.Delete(RutaEstado)
                File.Move(tmp, RutaEstado)
            Catch ex As Exception
                AppLogger.Warn("[Riesgo] No se pudo guardar el estado: " & ex.Message)
            End Try
        End Sub

    End Module

End Namespace

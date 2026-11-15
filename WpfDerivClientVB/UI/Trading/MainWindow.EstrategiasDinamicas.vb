' ============================================================
' MainWindow.EstrategiasDinamicas.vb
' Partial Class del MainWindow — Tab "Estrategias Dinamicas"
'
' Fuente de velas:
'   - Replay BD  : lee velas de PostgreSQL candle a candle
'                  (sin trades reales, usa el rango Desde/Hasta)
'   - Streaming  : recibe velas del WebSocket activo via
'                  el evento OnCandleCerrada (trades reales demo)
' ============================================================
Imports System.Collections.Generic
Imports System.Net.Http
Imports System.Text
Imports System.Threading.Tasks
Imports System.Windows.Media
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports Npgsql
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting

Namespace WpfDerivClientVB

    Partial Public Class MainWindow

        ' ── Estado del tab ──────────────────────────────────────────────
        Private _dinUltimoSesionId As Integer = 0
        Private _dinLiveActivo As Boolean = False
        Private _dinRefreshTimer As System.Windows.Threading.DispatcherTimer = Nothing
        Private _dinReplayMotor As LiveTestMotor = Nothing
        Private _dinPollTimer As System.Windows.Threading.DispatcherTimer = Nothing
        Private _dinUltimoSesionIdBD As Integer = 0   ' ultimo ID conocido en BD para detectar nuevas sesiones
        ' Guard: evita que eventos de XAML init disparen logica antes de que los controles existan
        Private _dinTabListo As Boolean = False
        ' Lista completa de sesiones cargadas (sin filtro), para poder filtrar por activo en memoria
        Private _dinSesionesCompletas As List(Of SesionRow) = New List(Of SesionRow)()
        ' Panel lateral de estrategias activas (instanciado en runtime)
        Private _panelLive As EstrategiasLivePanelControl = Nothing

        ' ── Inicializacion ──────────────────────────────────────────────
        Private Sub InicializarTabDinamicas()
            dpDinDesde.SelectedDate = DateTime.Today.AddDays(-30)
            dpDinHasta.SelectedDate = DateTime.Today
            EstrategiasDinamicasRegistry.Inicializar()
            CargarListaEstrategias()
            _dinTabListo = True
            Dispatcher.BeginInvoke(New Action(Async Sub()
                                                  Await CargarActivosBDAsync()
                                                  Await CargarSesionesAsync()
                                              End Sub), System.Windows.Threading.DispatcherPriority.Loaded)
            ' ── Polling: detecta sesiones nuevas creadas via API/MCP cada 5s ──
            _dinPollTimer = New System.Windows.Threading.DispatcherTimer()
            _dinPollTimer.Interval = TimeSpan.FromSeconds(5)
            AddHandler _dinPollTimer.Tick, AddressOf OnPollNuevaSesionTick
            _dinPollTimer.Start()

            ' ── Panel lateral de estrategias activas: solo wiring, sin dummies ───
            ' Las tarjetas se agregan cuando el usuario inicia una estrategia (live o replay).
            CablearPanelLive()
        End Sub

        Private Sub CargarListaEstrategias()
            lstEstrategias.Items.Clear()
            Try
                Dim estrategias = EstrategiasDinamicasRegistry.GetAll()
                For Each est In estrategias
                    lstEstrategias.Items.Add(est.Nombre)
                Next
                If estrategias.Count > 0 Then
                    lstEstrategias.SelectedIndex = 0
                End If
            Catch ex As Exception
                ActualizarLogBD("Error cargando estrategias: " & ex.Message, Colors.Salmon)
            End Try
        End Sub

        ' ── Cambio de fuente ─────────────────────────────────────────────
        Private Async Sub cmbDinFuente_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            ' Guard: este evento puede dispararse durante InitializeComponent() cuando
            ' IsSelected="True" activa la seleccion inicial, antes de que los controles
            ' del tab esten creados. Ignoramos hasta que InicializarTabDinamicas termine.
            If Not _dinTabListo Then Return
            Dim fuente = ObtenerFuente()
            If fuente = "replay" Then
                Await CargarActivosBDAsync()
            Else
                ' Modo Streaming: el activo se escribe manualmente
                ActualizarLogBD("Streaming: el activo se obtiene del WebSocket activo. Asegurate de iniciar el streaming.", Colors.CornflowerBlue)
            End If
        End Sub

        ' ── Cargar activos disponibles en BD ─────────────────────────────
        Private Async Function CargarActivosBDAsync() As Task
            ActualizarLogBD("Consultando activos en BD...", Colors.Gray)
            Try
                Dim cs = ConfigManager.LoadDbConfig().GetConnectionString()
                Dim resumen As New List(Of String)()
                Dim simbolos As New List(Of String)()

                Using conn As New NpgsqlConnection(cs)
                    Await conn.OpenAsync()
                    Dim sql = "SELECT symbol, timeframe, COUNT(*) AS total, " &
                              "MIN(open_time) AS desde, MAX(open_time) AS hasta " &
                              "FROM public.candles WHERE LOWER(platform) = 'deriv' " &
                              "GROUP BY symbol, timeframe ORDER BY symbol, timeframe"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            While Await reader.ReadAsync()
                                Dim sym = reader.GetString(0)
                                Dim tf = reader.GetString(1)
                                Dim total = reader.GetInt64(2)
                                Dim desde = reader.GetDateTime(3)
                                Dim hasta = reader.GetDateTime(4)
                                If Not simbolos.Contains(sym) Then simbolos.Add(sym)
                                resumen.Add(String.Format("{0}/{1}: {2:N0}v ({3:dd/MM/yy}-{4:dd/MM/yy})",
                                                          sym, tf, total, desde, hasta))
                            End While
                        End Using
                    End Using
                End Using

                ' Actualizar ComboBox de activos en el UI thread
                Dim prevSym = cmbDinSymbol.Text
                cmbDinSymbol.Items.Clear()
                For Each s In simbolos
                    cmbDinSymbol.Items.Add(s)
                Next
                ' Conservar seleccion previa o seleccionar el primero
                If simbolos.Contains(prevSym) Then
                    cmbDinSymbol.Text = prevSym
                ElseIf simbolos.Count > 0 Then
                    cmbDinSymbol.SelectedIndex = 0
                End If

                ' Actualizar ComboBox de filtro de sesiones con los mismos activos.
                ' Primer item vacio = "(todos)": muestra todas las sesiones sin filtrar.
                If cmbDinFiltroSimbolo IsNot Nothing Then
                    Dim prevFiltro = If(cmbDinFiltroSimbolo.SelectedItem?.ToString(), "")
                    cmbDinFiltroSimbolo.Items.Clear()
                    cmbDinFiltroSimbolo.Items.Add("")    ' = todos
                    For Each s In simbolos
                        cmbDinFiltroSimbolo.Items.Add(s)
                    Next
                    If simbolos.Contains(prevFiltro) Then
                        cmbDinFiltroSimbolo.SelectedItem = prevFiltro
                    Else
                        cmbDinFiltroSimbolo.SelectedIndex = 0   ' "" = todos
                    End If
                End If

                If resumen.Count = 0 Then
                    ActualizarLogBD("BD OK | Sin datos en la tabla candles (platform=deriv). Descarga historico primero.", Colors.Orange)
                Else
                    ActualizarLogBD("BD OK | " & String.Join("  |  ", resumen), Colors.LightGreen)
                End If

            Catch ex As Exception
                ActualizarLogBD("Error BD: " & ex.Message, Colors.Salmon)
            End Try
        End Function

        ' ── Helpers UI ──────────────────────────────────────────────────
        Private Sub ActualizarLogBD(texto As String, color As Color)
            ' Null-check defensivo: el control puede no estar listo si se llama
            ' muy temprano (ej. desde un evento disparado durante InitializeComponent).
            If txtDinLogBD Is Nothing Then Return
            txtDinLogBD.Text = texto
            txtDinLogBD.Foreground = New SolidColorBrush(color)
        End Sub

        Private Function ObtenerFuente() As String
            Dim item = TryCast(cmbDinFuente.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Return If(item IsNot Nothing, item.Tag?.ToString(), "replay")
        End Function

        Private Function ObtenerSymbol() As String
            Return If(cmbDinSymbol.Text, "").Trim()
        End Function

        Private Function ObtenerTimeframe() As String
            Dim item = TryCast(cmbDinTimeframe.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Return If(item IsNot Nothing, item.Tag?.ToString(), "1h")
        End Function

        Private Shared Function FechaAEpoch(fecha As DateTime) As Long
            Return New DateTimeOffset(fecha.Year, fecha.Month, fecha.Day,
                                      0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()
        End Function

        ' ── Eventos de UI ───────────────────────────────────────────────
        Private Async Sub lstEstrategias_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            If Not _dinTabListo Then Return
            Dim nombre = If(lstEstrategias.SelectedItem?.ToString(), "")
            dgDinOperaciones.ItemsSource = Nothing
            ' Si "Ver todas las estrategias" esta marcado, no filtrar por estrategia
            Dim filtroEst = If(chkDinTodasEstrategias?.IsChecked = True, "", nombre)
            Await CargarSesionesAsync(filtroEst)
        End Sub

        ''' <summary>
        ''' Toggle del checkbox: recarga la lista de sesiones con o sin filtro de estrategia.
        ''' </summary>
        Private Async Sub chkDinTodasEstrategias_Changed(sender As Object, e As System.Windows.RoutedEventArgs)
            If Not _dinTabListo Then Return
            Dim nombre = If(lstEstrategias.SelectedItem?.ToString(), "")
            Dim filtroEst = If(chkDinTodasEstrategias.IsChecked = True, "", nombre)
            Await CargarSesionesAsync(filtroEst)
        End Sub

        Private Async Sub btnRecargarEstrategias_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            EstrategiasDinamicasRegistry.Recargar()
            CargarListaEstrategias()
            If ObtenerFuente() = "replay" Then
                Await CargarActivosBDAsync()
            End If
        End Sub

        Private Async Sub btnDinBacktest_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await EjecutarBacktestAsync()
        End Sub

        Private Async Sub btnDinLiveIniciar_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await IniciarLiveAsync()
        End Sub

        Private Async Sub btnDinLiveDetener_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await DetenerLiveAsync()
        End Sub

    End Class

    ' ── DTO para la lista de sesiones ────────────────────────────────────
    Public Class SesionRow
        Public Property Id As Integer
        Public Property IdCorto As String          ' "#38"
        Public Property FechaHora As String          ' "28/04 14:32"
        Public Property WinRate As String          ' "62.5%"
        Public Property WinBrush As SolidColorBrush
        Public Property Resumen As String          ' "EmaRsiCruce · R_100/1h · 142ops · +$85.20"
        ' Campos de configuracion (para la barra de detalle al seleccionar)
        Public Property Estrategia As String
        Public Property Symbol As String
        Public Property Timeframe As String
        Public Property DuracionV As Integer
        Public Property PayoutPct As Decimal
        Public Property MinConf As Integer
        Public Property DesdeStr As String
        Public Property HastaStr As String
        Public Property TotalSenales As Long
        Public Property TotalOps As Long
        Public Property ProfitLoss As Decimal
        Public Property ProfitFactor As Decimal
        Public Property WinRateNum As Decimal         ' valor numerico para colorear
    End Class

    ' ── DTO para el DataGrid ─────────────────────────────────────────────
    Public Class OperacionRow
        Public Property Estrategia As String
        Public Property Version As String
        Public Property Tipo As String
        Public Property Epoch As String   ' apertura formateada "dd/MM/yyyy HH:mm"
        Public Property EpochSalida As String   ' cierre formateado  "dd/MM/yyyy HH:mm"
        Public Property Entrada      As String   ' formateado "{0:F5}"
        Public Property Salida       As String   ' formateado "{0:F5}"
        Public Property Estado       As String
        Public Property Pnl          As String   ' formateado "{0:+0.00;-0.00}"
        Public Property Confianza    As String   ' formateado "xx%"
        Public Property BalanceInicio As String
        Public Property Balance      As String
        Public Property Cuenta       As String
    End Class

End Namespace

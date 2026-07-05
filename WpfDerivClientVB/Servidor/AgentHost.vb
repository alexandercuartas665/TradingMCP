' ============================================================
' AgentHost.vb
'
' Punto de entrada del "modo agente": permite que un operador
' externo (Claude, scripts, CI) controle la aplicacion sin tocar
' la UI. Se activa con argumentos de linea de comandos:
'
'   WpfDerivClientVB.exe --heavy-api            (API admin en 8766)
'   WpfDerivClientVB.exe --heavy-api=8790       (puerto custom)
'   WpfDerivClientVB.exe --signalr-connect      (conecta cliente al hub con config guardada)
'   WpfDerivClientVB.exe --open-trading         (abre MainWindow minimizada -> registra
'                                                TradingService y permite conectar WS demo)
'   WpfDerivClientVB.exe --minimized            (AdminDashboard arranca minimizado)
'
' Los servicios creados aqui son GLOBALES (viven mas alla de las
' ventanas). La ventana HeavyStrategyTester ADOPTA la cola y el
' server globales si existen, para que UI y API vean lo mismo.
' ============================================================
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Threading
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting

Namespace WpfDerivClientVB.Servidor

    Public Module AgentHost

        Private ReadOnly _lock As New Object()
        Private _queue As HeavyTestQueue = Nothing
        Private _heavyApi As HeavyApiServer = Nothing

        ''' <summary>API admin global (Nothing si no se ha levantado).</summary>
        Public ReadOnly Property HeavyApi As HeavyApiServer
            Get
                SyncLock _lock
                    Return _heavyApi
                End SyncLock
            End Get
        End Property

        ''' <summary>
        ''' Cola de backtests unica de la app. La usan tanto el API headless como
        ''' la ventana HeavyStrategyTester (asi ambos ven los mismos jobs).
        ''' Crear/pedir SOLO desde el UI thread (ObservableCollection tiene afinidad).
        ''' </summary>
        Public Function ObtenerQueue() As HeavyTestQueue
            SyncLock _lock
                If _queue Is Nothing Then _queue = New HeavyTestQueue()
                Return _queue
            End SyncLock
        End Function

        ''' <summary>
        ''' Procesa los argumentos CLI. Llamar desde App.OnStartup (UI thread).
        ''' </summary>
        Public Sub IniciarDesdeArgs(args As String())
            If args Is Nothing Then args = New String() {}
            Dim lista = args.Select(Function(a) a.Trim().ToLowerInvariant()).ToList()

            ' ── --heavy-api[=port] ──────────────────────────────────────
            Dim argApi = lista.FirstOrDefault(Function(a) a.StartsWith("--heavy-api"))
            If argApi IsNot Nothing Then
                Dim port As Integer = 8766
                Dim idx = argApi.IndexOf("="c)
                If idx > 0 Then Integer.TryParse(argApi.Substring(idx + 1), port)
                If port < 1024 OrElse port > 65535 Then port = 8766
                IniciarHeavyApi(port)
            End If

            ' ── --signalr-connect ───────────────────────────────────────
            If lista.Contains("--signalr-connect") Then
                ConectarSignalRConConfig()
            End If

            ' ── --open-trading ──────────────────────────────────────────
            If lista.Contains("--open-trading") Then
                ' Diferir hasta que el Application termine de inicializar
                Application.Current.Dispatcher.BeginInvoke(
                    Sub() AbrirVentanaTrading(minimizada:=True),
                    DispatcherPriority.Loaded)
            End If

            ' ── --minimized ─────────────────────────────────────────────
            If lista.Contains("--minimized") Then
                Application.Current.Dispatcher.BeginInvoke(
                    Sub()
                        Try
                            If Application.Current.MainWindow IsNot Nothing Then
                                Application.Current.MainWindow.WindowState = WindowState.Minimized
                            End If
                        Catch
                        End Try
                    End Sub, DispatcherPriority.Loaded)
            End If
        End Sub

        ''' <summary>
        ''' Levanta el API admin global en el puerto dado (idempotente).
        ''' No requiere ninguna ventana abierta.
        ''' </summary>
        Public Sub IniciarHeavyApi(port As Integer)
            SyncLock _lock
                If _heavyApi IsNot Nothing AndAlso _heavyApi.IsRunning Then Return
            End SyncLock

            Try
                Dim q = ObtenerQueue()
                Dim srv As New HeavyApiServer(port) With {
                    .Queue = q,
                    .Repo = New BacktestRepository(),
                    .ConnectionString = ConfigManager.LoadDbConfig().GetConnectionString(),
                    .UiDispatcher = Application.Current.Dispatcher,
                    .LogAction = Sub(m) AppLogger.Info("[AgentHost] " & m),
                    .StartEjecucion = Function(conc) q.EjecutarTodasAsync(conc)
                }
                srv.Start()
                SyncLock _lock
                    _heavyApi = srv
                End SyncLock
                AppLogger.Info(String.Format("[AgentHost] Heavy API global iniciado en puerto {0} (modo agente).", port))
            Catch ex As Exception
                AppLogger.Warn("[AgentHost] No se pudo iniciar Heavy API: " & ex.Message)
            End Try
        End Sub

        ''' <summary>Conecta el cliente SignalR usando la configuracion persistida.</summary>
        Public Sub ConectarSignalRConConfig()
            Try
                Dim cfg = ConfigManager.LoadDbConfig()
                If String.IsNullOrWhiteSpace(cfg.SignalRUrl) Then
                    AppLogger.Warn("[AgentHost] --signalr-connect: no hay SignalRUrl en config. Omitido.")
                    Return
                End If
                Dim cid = If(String.IsNullOrWhiteSpace(cfg.SignalRClientId), Environment.MachineName, cfg.SignalRClientId)
                Dim hub = If(String.IsNullOrWhiteSpace(cfg.SignalRHubName), SignalRClientService.HUB_NAME_DEFAULT, cfg.SignalRHubName)
                Dim _t = SignalRClientService.GetInstance().ConectarAsync(cfg.SignalRUrl, cid, hub)
                AppLogger.Info(String.Format("[AgentHost] SignalR conectando a {0} (clientId={1}, hub={2})...",
                                              cfg.SignalRUrl, cid, hub))
            Catch ex As Exception
                AppLogger.Warn("[AgentHost] Error conectando SignalR: " & ex.Message)
            End Try
        End Sub

        ''' <summary>
        ''' Abre la ventana de trading (MainWindow) para que registre el
        ''' TradingService y se pueda conectar el WS demo. Idempotente: si ya
        ''' hay una abierta no crea otra.
        ''' </summary>
        Public Sub AbrirVentanaTrading(Optional minimizada As Boolean = False)
            Try
                For Each w As Window In Application.Current.Windows
                    If TypeOf w Is MainWindow Then
                        If Not minimizada Then w.Activate()
                        Return
                    End If
                Next
                Dim mw As New MainWindow()
                If minimizada Then mw.WindowState = WindowState.Minimized
                mw.Show()
                AppLogger.Info("[AgentHost] MainWindow abierta (TradingService registrado).")
            Catch ex As Exception
                AppLogger.Warn("[AgentHost] Error abriendo MainWindow: " & ex.Message)
            End Try
        End Sub

        ''' <summary>Abre la ventana ServidorLive. Idempotente.</summary>
        Public Sub AbrirVentanaServidorLive()
            Try
                For Each w As Window In Application.Current.Windows
                    If TypeOf w Is ServidorLiveWindow Then
                        w.Activate()
                        Return
                    End If
                Next
                Dim v As New ServidorLiveWindow()
                v.Show()
            Catch ex As Exception
                AppLogger.Warn("[AgentHost] Error abriendo ServidorLive: " & ex.Message)
            End Try
        End Sub

        ''' <summary>Abre la ventana HeavyStrategyTester. Idempotente.</summary>
        Public Sub AbrirVentanaHeavy()
            Try
                For Each w As Window In Application.Current.Windows
                    If TypeOf w Is HeavyStrategyTesterWindow Then
                        w.Activate()
                        Return
                    End If
                Next
                Dim v As New HeavyStrategyTesterWindow()
                v.Show()
            Catch ex As Exception
                AppLogger.Warn("[AgentHost] Error abriendo HeavyTester: " & ex.Message)
            End Try
        End Sub

    End Module

End Namespace

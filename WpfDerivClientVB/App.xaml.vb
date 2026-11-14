Imports System.Windows
Imports System.Threading.Tasks

Namespace WpfDerivClientVB

    ''' <summary>
    ''' Clase de inicio de la aplicación WPF.
    ''' Responsabilidades:
    '''   1. Inicializar el sistema de logging persistente.
    '''   2. Registrar handlers para capturar cierres inesperados (crashes).
    ''' </summary>
    Partial Public Class App
        Inherits Application

        Protected Overrides Sub OnStartup(e As StartupEventArgs)
            MyBase.OnStartup(e)

            ' ── Inicializar logger primero ──────────────────────────────
            AppLogger.Initialize()
            AppLogger.Info("Aplicación iniciando...")

            ' ── Handler 1: Excepciones en el thread de UI (WPF Dispatcher) ──
            AddHandler Me.DispatcherUnhandledException,
                Sub(sender As Object, args As System.Windows.Threading.DispatcherUnhandledExceptionEventArgs)
                    AppLogger.Error("CRASH no manejado en UI thread", args.Exception)
                    ' Marcar como manejado para que WPF no muestre el dialogo generico
                    args.Handled = True
                    Dim msg As String = String.Format(
                        "Error inesperado:{0}{1}{0}{0}Revisa el log en:{0}{2}",
                        Environment.NewLine, args.Exception.Message, AppLogger.GetLogPath())
                    MessageBox.Show(msg, "Error en la aplicación",
                                    MessageBoxButton.OK, MessageBoxImage.Error)
                End Sub

            ' ── Handler 2: Excepciones en threads de background ──────────
            AddHandler AppDomain.CurrentDomain.UnhandledException,
                Sub(sender As Object, args As UnhandledExceptionEventArgs)
                    Dim ex As Exception = TryCast(args.ExceptionObject, Exception)
                    If ex IsNot Nothing Then
                        AppLogger.Error("CRASH no manejado en background thread (IsTerminating=" &
                                        args.IsTerminating.ToString() & ")", ex)
                    Else
                        AppLogger.Error("CRASH background thread: objeto no es Exception: " &
                                        args.ExceptionObject.ToString())
                    End If
                End Sub

            ' ── Handler 3: Tasks async sin observar (fire-and-forget sin catch) ──
            AddHandler TaskScheduler.UnobservedTaskException,
                Sub(sender As Object, args As UnobservedTaskExceptionEventArgs)
                    For Each inner In args.Exception.InnerExceptions
                        AppLogger.Error("Task async no observada", inner)
                    Next
                    args.SetObserved()  ' Evita que el proceso termine
                End Sub

            AppLogger.Info("Handlers de excepción globales registrados OK")

            ' ── Seguridad: token de las APIs locales 8765/8766 (se crea una vez).
            Try
                AgentAuth.GetOrCreateToken()
            Catch ex As Exception
                AppLogger.Warn("[App] No se pudo preparar el token de API local: " & ex.Message)
            End Try

            ' ── Seguridad: cifrar con DPAPI los tokens de Deriv que sigan en
            '    texto plano en la BD (idempotente, en segundo plano).
            Task.Run(Async Function()
                         Try
                             Await New ClientRepository().EnsureTablesExistAsync()
                         Catch ex As Exception
                             AppLogger.Warn("[App] Migracion de cifrado de tokens: " & ex.Message)
                         End Try
                     End Function)

            ' ── Singletons del modulo Servidor (cliente SignalR + controller live) ──
            ' Subscribir CommandRouter al evento de instrucciones del cliente SignalR.
            ' Esto se hace UNA sola vez para toda la app; los servicios viven mas alla
            ' de las ventanas que abre/cierra el usuario.
            Try
                Dim signalRSvc = WpfDerivClientVB.Servidor.SignalRClientService.GetInstance()
                AddHandler signalRSvc.InstruccionRecibida,
                    AddressOf WpfDerivClientVB.Servidor.CommandRouter.Handle
                AddHandler signalRSvc.BroadcastRecibido,
                    Sub(instruccion As String, parametros As String, timestamp As String)
                        ' Broadcast = misma logica pero sin clienteId ni correlationId
                        WpfDerivClientVB.Servidor.CommandRouter.Handle("ALL", instruccion, parametros, "", timestamp)
                    End Sub

                ' Forzar instanciacion del controller de estrategias para que ya este listo
                ' cuando lleguen las primeras instrucciones del servidor.
                Dim _ctrl = WpfDerivClientVB.Servidor.LiveStrategiesController.GetInstance()
                AppLogger.Info("Modulo Servidor (SignalR + LiveCtrl) inicializado.")
            Catch ex As Exception
                AppLogger.Warn("[App] No se pudo inicializar modulo Servidor: " & ex.Message)
            End Try

            ' ── Modo agente: argumentos CLI (--heavy-api, --signalr-connect,
            '    --open-trading, --minimized). Permite control externo total
            '    sin interaccion con la UI. Ver Servidor/AgentHost.vb.
            Try
                WpfDerivClientVB.Servidor.AgentHost.IniciarDesdeArgs(e.Args)
            Catch ex As Exception
                AppLogger.Warn("[App] AgentHost: " & ex.Message)
            End Try
        End Sub

        Protected Overrides Sub OnExit(e As ExitEventArgs)
            AppLogger.Info(String.Format("Aplicación cerrando (código: {0})", e.ApplicationExitCode))
            MyBase.OnExit(e)
        End Sub

    End Class

End Namespace

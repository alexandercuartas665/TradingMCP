Imports System.IO
Imports System.Text
Imports System.Threading

Namespace WpfDerivClientVB

    ''' <summary>
    ''' Logger de aplicación thread-safe que escribe a archivo en disco.
    ''' Directorio de logs: [ExeDir]\logs\WpfDerivClientVB_YYYY-MM-DD.log
    '''
    ''' Uso:
    '''   AppLogger.Info("Mensaje informativo")
    '''   AppLogger.Warn("Algo sospechoso")
    '''   AppLogger.Error("Error grave", optionalException)
    '''
    ''' El archivo de log persiste entre ejecuciones y permite diagnosticar
    ''' cierres inesperados de la aplicación.
    ''' </summary>
    Public Module AppLogger

        Private ReadOnly _lock As New Object()
        Private _logFilePath As String = Nothing

        ' ================================================================
        '  INICIALIZACIÓN — llamar desde App.xaml.vb al arrancar
        ' ================================================================

        ''' <summary>
        ''' Inicializa el logger creando el directorio y archivo de log.
        ''' Debe llamarse una vez al inicio de la aplicación.
        ''' </summary>
        Public Sub Initialize()
            Try
                Dim exeDir As String = AppDomain.CurrentDomain.BaseDirectory
                Dim logsDir As String = Path.Combine(exeDir, "logs")
                Directory.CreateDirectory(logsDir)
                Dim fileName As String = String.Format("WpfDerivClientVB_{0}.log",
                                                        DateTime.Now.ToString("yyyy-MM-dd"))
                _logFilePath = Path.Combine(logsDir, fileName)
                Info("========== Sesión iniciada ==========")
                Info(String.Format("Log en: {0}", _logFilePath))
            Catch ex As Exception
                ' Si falla al inicializar el logger, al menos escribimos a consola
                Console.WriteLine("[AppLogger.Initialize] Error: " & ex.Message)
            End Try
        End Sub

        ' ================================================================
        '  MÉTODOS DE LOG PÚBLICOS
        ' ================================================================

        ''' <summary>Registra un mensaje de nivel INFO.</summary>
        Public Sub Info(message As String)
            WriteLog("INFO ", message, Nothing)
        End Sub

        ''' <summary>Registra un mensaje de nivel WARN.</summary>
        Public Sub Warn(message As String)
            WriteLog("WARN ", message, Nothing)
        End Sub

        ''' <summary>Registra un mensaje de nivel ERROR, opcionalmente con excepción completa.</summary>
        Public Sub [Error](message As String, Optional ex As Exception = Nothing)
            WriteLog("ERROR", message, ex)
        End Sub

        ''' <summary>Retorna la ruta completa del archivo de log activo.</summary>
        Public Function GetLogPath() As String
            Return If(_logFilePath, "(Logger no inicializado)")
        End Function

        ' ================================================================
        '  LÓGICA INTERNA
        ' ================================================================

        Private Sub WriteLog(level As String, message As String, ex As Exception)
            Try
                Dim timestamp As String = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                Dim sb As New StringBuilder()
                sb.AppendLine(String.Format("[{0}] [{1}] {2}", timestamp, level, message))

                If ex IsNot Nothing Then
                    sb.AppendLine(String.Format("           Exception : {0}", ex.GetType().FullName))
                    sb.AppendLine(String.Format("           Message   : {0}", ex.Message))
                    If ex.InnerException IsNot Nothing Then
                        sb.AppendLine(String.Format("           Inner     : {0}", ex.InnerException.Message))
                    End If
                    sb.AppendLine(String.Format("           StackTrace: {0}", ex.StackTrace))
                End If

                ' Siempre escribir a consola (visible en debug)
                Console.Write(sb.ToString())

                ' Escribir a archivo si está inicializado
                If _logFilePath IsNot Nothing Then
                    SyncLock _lock
                        File.AppendAllText(_logFilePath, sb.ToString(), Encoding.UTF8)
                    End SyncLock
                End If
            Catch
                ' El logger nunca debe causar excepciones adicionales
            End Try
        End Sub

    End Module

End Namespace

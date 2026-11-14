Imports Newtonsoft.Json
Imports System.IO

Namespace WpfDerivClientVB

    ' Define la estructura de datos que irá a db_config.json
    Public Class PostgresConfig
        Public Property Host As String = "localhost"
        Public Property Port As Integer = 8083
        Public Property Database As String = "traiding_db"
        Public Property Username As String = "traiding_user"
        Public Property Password As String = ""
        ''' <summary>API key para servicios externos (notificaciones, integraciones, etc.).</summary>
        Public Property ApiKey As String = ""
        ''' <summary>Endpoint base de la API externa (ej. https://api.servicio.com).</summary>
        Public Property Endpoint As String = ""

        ''' <summary>URL del servidor SignalR de control remoto (ej. https://servidor.com/signalr).</summary>
        Public Property SignalRUrl As String = ""
        ''' <summary>Identificador unico de este cliente para registrarse en el servidor.</summary>
        Public Property SignalRClientId As String = ""
        ''' <summary>Nombre del Hub en el servidor (debe coincidir con la implementacion remota). Default DronesHub para compatibilidad con servidores existentes tipo Doom.</summary>
        Public Property SignalRHubName As String = "DronesHub"
        ''' <summary>Si True, conecta automaticamente al abrir la ventana Servidor Live.</summary>
        Public Property AutoConectarSignalR As Boolean = False

        ''' <summary>
        ''' Secreto compartido para firmar (HMAC-SHA256) las instrucciones que llegan
        ''' por SignalR. Sin el, las instrucciones que modifican estado se rechazan.
        ''' </summary>
        Public Property SignalRSecret As String = ""

        ''' <summary>
        ''' Si False (por defecto), la cuenta REAL no se puede conectar desde las APIs
        ''' locales ni desde SignalR: solo desde la UI, con confirmacion.
        ''' </summary>
        Public Property PermitirRealRemoto As Boolean = False

        ' ── Gestor de riesgo (ver Infrastructure/RiskManager.vb) ──
        ''' <summary>Stake maximo por operacion automatica (USD).</summary>
        Public Property RiesgoMontoMaximo As Decimal = 2D
        ''' <summary>Perdida maxima del dia UTC (USD, resultados reales de Deriv). 0 = sin limite.</summary>
        Public Property RiesgoPerdidaMaximaDiaria As Decimal = 10D
        ''' <summary>Contratos abiertos a la vez (incluye compras en curso). 0 = sin limite.</summary>
        Public Property RiesgoMaxContratosAbiertos As Integer = 3
        ''' <summary>Si False (por defecto), las operaciones automaticas solo se ejecutan en cuenta DEMO.</summary>
        Public Property PermitirRealAutomatico As Boolean = False

        ''' <summary>
        ''' Genera la cadena de conexión basada en los parámetros actuales.
        ''' </summary>
        Public Function GetConnectionString() As String
            Return $"Host={Host};Port={Port};Database={Database};Username={Username};Password={Password};" &
                   "Pooling=true;Minimum Pool Size=1;Maximum Pool Size=10;"
        End Function
    End Class

    ''' <summary>
    ''' Clase central estática/módulo encargada de guardar y cargar parámetros en db_config.json.
    ''' </summary>
    Public Module ConfigManager

        Private ReadOnly DbConfigPath As String = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "db_config.json")

        ''' <summary>
        ''' Carga la configuración de base de datos desde el archivo físico.
        ''' </summary>
        Public Function LoadDbConfig() As PostgresConfig
            Try
                If File.Exists(DbConfigPath) Then
                    Dim json As String = File.ReadAllText(DbConfigPath)
                    Dim config As PostgresConfig = JsonConvert.DeserializeObject(Of PostgresConfig)(json)
                    If config IsNot Nothing Then
                        Return config
                    End If
                End If
            Catch ex As Exception
                ' Si hay fallo de deserializado, retornamos los default
            End Try

            ' Si no existe el archivo o falló algo, retorna los valores predeterminados
            Return New PostgresConfig()
        End Function

        ''' <summary>
        ''' Persiste la configuración de la base de datos en db_config.json.
        ''' </summary>
        Public Sub SaveDbConfig(config As PostgresConfig)
            Try
                Dim json As String = JsonConvert.SerializeObject(config, Formatting.Indented)
                File.WriteAllText(DbConfigPath, json)
            Catch ex As Exception
                Throw New Exception("Error al guardar la configuración de BD: " & ex.Message)
            End Try
        End Sub

    End Module

End Namespace

Imports Newtonsoft.Json
Imports System.IO

Namespace WpfDerivClientVB

    ' Define la estructura de datos que irá a db_config.json
    Public Class PostgresConfig
        Public Property Host As String = "localhost"
        Public Property Port As Integer = 8083
        Public Property Database As String = "traiding_db"
        Public Property Username As String = "traiding_user"
        Public Property Password As String = "traiding_pass"

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

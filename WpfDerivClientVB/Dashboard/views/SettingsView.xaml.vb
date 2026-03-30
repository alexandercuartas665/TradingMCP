Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports Npgsql
Imports WpfDerivClientVB.Infrastructure

Namespace WpfDerivClientVB

    Partial Public Class SettingsView
        Inherits UserControl

        Public Sub New()
            InitializeComponent()
            CargarConfiguracion()
        End Sub

        ''' <summary>
        ''' Lee la configuración actual desde db_config.json y la llena en los campos.
        ''' </summary>
        Private Sub CargarConfiguracion()
            Dim conf = ConfigManager.LoadDbConfig()
            
            txtHost.Text = conf.Host
            txtPort.Text = conf.Port.ToString()
            txtDatabase.Text = conf.Database
            txtUsername.Text = conf.Username
            txtPassword.Password = conf.Password
        End Sub

        ''' <summary>
        ''' Construye un modelo PostgresConfig con los inputs actuales.
        ''' </summary>
        Private Function ObtenerConfigDeUI() As PostgresConfig
            Dim puerto As Integer = 5432
            Integer.TryParse(txtPort.Text, puerto)
            
            Return New PostgresConfig With {
                .Host = txtHost.Text.Trim(),
                .Port = puerto,
                .Database = txtDatabase.Text.Trim(),
                .Username = txtUsername.Text.Trim(),
                .Password = txtPassword.Password
            }
        End Function

        ''' <summary>
        ''' Botón para guardar configuración.
        ''' </summary>
        Private Sub btnSave_Click(sender As Object, e As RoutedEventArgs)
            Try
                Dim nuevaConfig = ObtenerConfigDeUI()
                ConfigManager.SaveDbConfig(nuevaConfig)
                
                MostrarEstado("Configuración guardada en db_config.json exitosamente.", True)
            Catch ex As Exception
                MostrarEstado("Error al guardar: " & ex.Message, False)
            End Try
        End Sub

        ''' <summary>
        ''' Botón para probar la conexión con Npgsql de forma asíncrona.
        ''' </summary>
        Private Async Sub btnTestConnection_Click(sender As Object, e As RoutedEventArgs)
            btnTestConnection.IsEnabled = False
            btnTestConnection.Content = "Probando..."
            
            Dim conf = ObtenerConfigDeUI()
            Dim connString = conf.GetConnectionString()
            
            Try
                Using conn As New NpgsqlConnection(connString)
                    Await conn.OpenAsync()
                    MostrarEstado("Conexión exitosa al Clúster PostgreSQL.", True)
                End Using
            Catch ex As Exception
                MostrarEstado("Error de conexión: " & ex.Message, False)
            Finally
                btnTestConnection.IsEnabled = True
                btnTestConnection.Content = "Test Connection"
            End Try
        End Sub

        ''' <summary>
        ''' Muestra la alerta de éxito o error en la UI.
        ''' </summary>
        Private Sub MostrarEstado(mensaje As String, exito As Boolean)
            brdStatus.Visibility = Visibility.Visible
            txtStatus.Text = mensaje
            If exito Then
                brdStatus.Background = New SolidColorBrush(Color.FromArgb(25, 0, 212, 170)) ' Success low opacity
                brdStatus.BorderBrush = New SolidColorBrush(Color.FromRgb(0, 212, 170))
                iconStatus.Text = "✅"
                txtStatus.Foreground = New SolidColorBrush(Color.FromRgb(0, 212, 170))
            Else
                brdStatus.Background = New SolidColorBrush(Color.FromArgb(25, 255, 82, 82)) ' Error low opacity
                brdStatus.BorderBrush = New SolidColorBrush(Color.FromRgb(255, 82, 82))
                iconStatus.Text = "❌"
                txtStatus.Foreground = New SolidColorBrush(Color.FromRgb(255, 82, 82))
            End If
        End Sub

    End Class
End Namespace

' ============================================================
' AgentAuth.vb
'
' Autenticacion de las APIs locales (TradingApiServer :8765 y
' HeavyApiServer :8766).
'
' - Cada peticion debe traer el header  X-Agent-Token: <token>.
' - El token se genera una sola vez (32 bytes aleatorios) y se
'   guarda en %LOCALAPPDATA%\WpfDerivClientVB\agent_token.txt
'   (carpeta del perfil del usuario). Los scripts lo leen de ahi
'   (ver agent_auth.ps1 en la raiz del repo).
' - Se rechaza cualquier peticion con header Origin: un navegador
'   lo envia siempre en peticiones cross-origin, asi que una pagina
'   web abierta no puede usar estas APIs.
' ============================================================
Imports System.IO
Imports System.Net
Imports System.Security.Cryptography
Imports System.Text

Namespace WpfDerivClientVB

    Public Module AgentAuth

        Public Const HeaderName As String = "X-Agent-Token"

        Private ReadOnly _lock As New Object()
        Private _token As String = Nothing

        ''' <summary>Ruta del archivo con el token.</summary>
        Public ReadOnly Property TokenPath As String
            Get
                Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "WpfDerivClientVB", "agent_token.txt")
            End Get
        End Property

        ''' <summary>Devuelve el token, creandolo la primera vez.</summary>
        Public Function GetOrCreateToken() As String
            SyncLock _lock
                If _token IsNot Nothing Then Return _token
                Try
                    If File.Exists(TokenPath) Then
                        Dim t = File.ReadAllText(TokenPath).Trim()
                        If t.Length >= 32 Then
                            _token = t
                            Return _token
                        End If
                    End If
                Catch ex As Exception
                    AppLogger.Warn("[AgentAuth] No se pudo leer el token: " & ex.Message)
                End Try

                Dim bytes(31) As Byte
                Using rng As New RNGCryptoServiceProvider()
                    rng.GetBytes(bytes)
                End Using
                Dim sb As New StringBuilder()
                For Each b In bytes
                    sb.Append(b.ToString("x2"))
                Next
                _token = sb.ToString()
                Directory.CreateDirectory(Path.GetDirectoryName(TokenPath))
                File.WriteAllText(TokenPath, _token)
                AppLogger.Info("[AgentAuth] Token de API local creado en " & TokenPath)
                Return _token
            End SyncLock
        End Function

        ''' <summary>
        ''' Valida la peticion. Devuelve "" si es valida o el motivo del rechazo.
        ''' </summary>
        Public Function Validar(req As HttpListenerRequest) As String
            If Not String.IsNullOrEmpty(req.Headers("Origin")) Then
                Return "Peticiones desde navegador (header Origin) no permitidas."
            End If
            Dim recibido = req.Headers(HeaderName)
            If String.IsNullOrEmpty(recibido) Then
                Return "Falta el header " & HeaderName & ". El token esta en " & TokenPath
            End If
            If Not IgualTiempoConstante(recibido.Trim(), GetOrCreateToken()) Then
                Return "Token invalido."
            End If
            Return ""
        End Function

        ''' <summary>Comparacion en tiempo constante (evita ataques de temporizacion).</summary>
        Public Function IgualTiempoConstante(a As String, b As String) As Boolean
            If a Is Nothing OrElse b Is Nothing Then Return False
            Dim ba = Encoding.UTF8.GetBytes(a)
            Dim bb = Encoding.UTF8.GetBytes(b)
            Dim diff As Integer = ba.Length Xor bb.Length
            For i = 0 To Math.Min(ba.Length, bb.Length) - 1
                diff = diff Or (CInt(ba(i)) Xor CInt(bb(i)))
            Next
            Return diff = 0
        End Function

    End Module

End Namespace

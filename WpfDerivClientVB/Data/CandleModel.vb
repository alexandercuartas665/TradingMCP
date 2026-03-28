Imports System.ComponentModel

Namespace WpfDerivClientVB
    Public Class CandleModel
        Implements INotifyPropertyChanged

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Private _openTime As String
        Private _openPrice As Decimal
        Private _high As Decimal
        Private _low As Decimal
        Private _closePrice As Decimal
        Private _status As String
        Private _epoch As Long

        Public Property Epoch As Long
            Get
                Return _epoch
            End Get
            Set(value As Long)
                _epoch = value
                Notify("Epoch")
            End Set
        End Property

        Public Property OpenTime As String
            Get
                Return _openTime
            End Get
            Set(value As String)
                _openTime = value
                Notify("OpenTime")
            End Set
        End Property

        ''' <summary>Precio de apertura (renombrado para evitar conflicto con Open reservado en VB)</summary>
        Public Property OpenPrice As Decimal
            Get
                Return _openPrice
            End Get
            Set(value As Decimal)
                _openPrice = value
                Notify("OpenPrice")
            End Set
        End Property

        Public Property High As Decimal
            Get
                Return _high
            End Get
            Set(value As Decimal)
                _high = value
                Notify("High")
            End Set
        End Property

        Public Property Low As Decimal
            Get
                Return _low
            End Get
            Set(value As Decimal)
                _low = value
                Notify("Low")
            End Set
        End Property

        Public Property ClosePrice As Decimal
            Get
                Return _closePrice
            End Get
            Set(value As Decimal)
                _closePrice = value
                Notify("ClosePrice")
            End Set
        End Property

        Public Property Status As String
            Get
                Return _status
            End Get
            Set(value As String)
                _status = value
                Notify("Status")
            End Set
        End Property

        Private Sub Notify(propertyName As String)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(propertyName))
        End Sub
    End Class
End Namespace

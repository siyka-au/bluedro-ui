//*********************************************************
//
// Copyright (c) Microsoft. All rights reserved.
// This code is licensed under the MIT License (MIT).
// THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
// IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
// PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.
//
//*********************************************************

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace BlueDRO
{
    // This scenario connects to the device selected in the "Discover
    // GATT Servers" scenario and communicates with it.
    // Note that this scenario is rather artificial because it communicates
    // with an unknown service with unknown characteristics.
    // In practice, your app will be interested in a specific service with
    // a specific characteristic.
    public sealed partial class Display : Page
    {
        private MainPage rootPage = MainPage.Current;

        private BluetoothLEDevice bluetoothLeDevice = null;

        private static Guid blueDroServiceUuid =            Guid.Parse("d7578caf-686d-4216-ba8e-a3703f1590fc");
        private static Guid positionCharacteristicUuid =    Guid.Parse("d757fcb0-686d-4216-ba8e-a3703f1590fc");
        private static Guid numeratorCharacteristicUuid =   Guid.Parse("d757fcb1-686d-4216-ba8e-a3703f1590fc");
        private static Guid denominatorCharacteristicUuid = Guid.Parse("d757fcb2-686d-4216-ba8e-a3703f1590fc");
        private static Guid reverseCharacteristicUuid =     Guid.Parse("d757fcb3-686d-4216-ba8e-a3703f1590fc");
        private static Guid setPositionCharacteristicUuid = Guid.Parse("d757fcb4-686d-4216-ba8e-a3703f1590fc");

        private GattDeviceService  blueDroService;
        private GattCharacteristic positionCharacteristic;
        private GattCharacteristic numeratorCharacteristic;
        private GattCharacteristic denominatorCharacteristic;
        private GattCharacteristic reverseCharacteristic;
        private GattCharacteristic setPositionCharacteristic;

        #region Error Codes
        readonly int E_BLUETOOTH_ATT_WRITE_NOT_PERMITTED = unchecked((int)0x80650003);
        readonly int E_BLUETOOTH_ATT_INVALID_PDU = unchecked((int)0x80650004);
        readonly int E_ACCESSDENIED = unchecked((int)0x80070005);
        readonly int E_DEVICE_NOT_AVAILABLE = unchecked((int)0x800710df); // HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_AVAILABLE)
        #endregion

        #region UI Code
        public Display()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            SelectedDeviceRun.Text = rootPage.SelectedBleDeviceName;
            if (string.IsNullOrEmpty(rootPage.SelectedBleDeviceId))
            {
                ConnectButton.IsEnabled = false;
            }
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            var success = await ClearBluetoothLEDeviceAsync();
            if (!success)
            {
                rootPage.NotifyUser("Error: Unable to reset app state", NotifyType.ErrorMessage);
            }
        }
        #endregion

        #region Enumerating Services
        private async Task<bool> ClearBluetoothLEDeviceAsync()
        {
            // Need to clear the CCCD from the remote device so we stop receiving notifications
            //var result = await positionCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
            //if (result != GattCommunicationStatus.Success)
            //{
            //    return false;
            //}
            //else
            //{
            //    positionCharacteristic.ValueChanged -= PositionCharacteristic_ValueChanged;
            //}
            bluetoothLeDevice?.Dispose();
            bluetoothLeDevice = null;
            return true;
        }

        private async void ConnectButton_Click()
        {
            ConnectButton.IsEnabled = false;

            if (!await ClearBluetoothLEDeviceAsync())
            {
                rootPage.NotifyUser("Error: Unable to reset state, try again.", NotifyType.ErrorMessage);
                ConnectButton.IsEnabled = true;
                return;
            }

            try
            {
                // BT_Code: BluetoothLEDevice.FromIdAsync must be called from a UI thread because it may prompt for consent.
                bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(rootPage.SelectedBleDeviceId);

                if (bluetoothLeDevice == null)
                {
                    rootPage.NotifyUser("Failed to connect to device.", NotifyType.ErrorMessage);
                }
            }
            catch (Exception ex) when (ex.HResult == E_DEVICE_NOT_AVAILABLE)
            {
                rootPage.NotifyUser("Bluetooth radio is not on.", NotifyType.ErrorMessage);
            }

            if (bluetoothLeDevice != null)
            {
                // Note: BluetoothLEDevice.GattServices property will return an empty list for unpaired devices. For all uses we recommend using the GetGattServicesAsync method.
                // BT_Code: GetGattServicesAsync returns a list of all the supported services of the device (even if it's not paired to the system).
                // If the services supported by the device are expected to change during BT usage, subscribe to the GattServicesChanged event.
                GattDeviceServicesResult result = await bluetoothLeDevice.GetGattServicesForUuidAsync(blueDroServiceUuid);
                
                if (result.Status == GattCommunicationStatus.Success)
                {
                    blueDroService = result.Services[0];

                    positionCharacteristic =    (await blueDroService.GetCharacteristicsForUuidAsync(positionCharacteristicUuid)).Characteristics[0];
                    numeratorCharacteristic =   (await blueDroService.GetCharacteristicsForUuidAsync(numeratorCharacteristicUuid)).Characteristics[0];
                    denominatorCharacteristic = (await blueDroService.GetCharacteristicsForUuidAsync(denominatorCharacteristicUuid)).Characteristics[0];
                    reverseCharacteristic =     (await blueDroService.GetCharacteristicsForUuidAsync(reverseCharacteristicUuid)).Characteristics[0];
                    setPositionCharacteristic = (await blueDroService.GetCharacteristicsForUuidAsync(setPositionCharacteristicUuid)).Characteristics[0];

                    // initialize status
                    GattCommunicationStatus status = GattCommunicationStatus.Unreachable;
                    var cccdValue = GattClientCharacteristicConfigurationDescriptorValue.None;
                    if (positionCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
                    {
                        cccdValue = GattClientCharacteristicConfigurationDescriptorValue.Indicate;
                    }
                    else if (positionCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                    {
                        cccdValue = GattClientCharacteristicConfigurationDescriptorValue.Notify;
                    }

                    try
                    {
                        // BT_Code: Must write the CCCD in order for server to send indications.
                        // We receive them in the ValueChanged event handler.
                        status = await positionCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(cccdValue);

                        if (status == GattCommunicationStatus.Success)
                        {
                            positionCharacteristic.ValueChanged += PositionCharacteristic_ValueChanged;
                            PositionValue.Text = String.Format("{0:F3}", 0).PadLeft(8);

                            rootPage.NotifyUser("Successfully subscribed for value changes", NotifyType.StatusMessage);
                        }
                        else
                        {
                            rootPage.NotifyUser($"Error registering for value changes: {status}", NotifyType.ErrorMessage);
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        // This usually happens when a device reports that it support indicate, but it actually doesn't.
                        rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                    }

                    ConnectButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    rootPage.NotifyUser("Device unreachable", NotifyType.ErrorMessage);
                }
            }
            ConnectButton.IsEnabled = true;
        }

        private async void PositionCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out byte[] data);
            var val = BitConverter.ToInt32(data, 0) / 1000.0;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                () => PositionValue.Text = String.Format("{0:F3}", val).PadLeft(8));
        }

        #endregion

        private async void SetPositionCharacteristicButton_Click()
        {
            if (!String.IsNullOrEmpty(SetPositionCharacteristicValue.Text))
            {
                var isValidValue = Double.TryParse(SetPositionCharacteristicValue.Text, out double readValue);
                if (isValidValue)
                {
                    var writer = new DataWriter{
                        ByteOrder = ByteOrder.LittleEndian
                    };
                    writer.WriteInt32(Convert.ToInt32(readValue * 1000));
                    await WriteBufferToSelectedCharacteristicAsync(writer.DetachBuffer());
                }
                else
                {
                    rootPage.NotifyUser("Set position has to be a valid number", NotifyType.ErrorMessage);
                }
            }
            else
            {
                rootPage.NotifyUser("No data to set position", NotifyType.ErrorMessage);
            }
        }

        private async Task<bool> WriteBufferToSelectedCharacteristicAsync(IBuffer buffer)
        {
            try
            {
                // BT_Code: Writes the value from the buffer to the characteristic.
                var result = await setPositionCharacteristic.WriteValueWithResultAsync(buffer);

                if (result.Status == GattCommunicationStatus.Success)
                {
                    rootPage.NotifyUser("Successfully set position", NotifyType.StatusMessage);
                    return true;
                }
                else
                {
                    rootPage.NotifyUser($"Set position failed: {result.Status} {result.ProtocolError}", NotifyType.ErrorMessage);
                    return false;
                }
            }
            catch (Exception ex) when (ex.HResult == E_BLUETOOTH_ATT_INVALID_PDU)
            {
                rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                return false;
            }
            catch (Exception ex) when (ex.HResult == E_BLUETOOTH_ATT_WRITE_NOT_PERMITTED || ex.HResult == E_ACCESSDENIED)
            {
                // This usually happens when a device reports that it support writing, but it actually doesn't.
                rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                return false;
            }
        }
    }
}

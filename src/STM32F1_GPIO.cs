//
// Copyright (c) 2010-2023 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    [AllowedTranslations(AllowedTranslation.WordToDoubleWord)]
    public class STM32F1_GPIO : BaseGPIOPort, IDoubleWordPeripheral
    {
        public STM32F1_GPIO(IMachine machine) : base(machine, NumberOfPorts)
        {
            pinMode = new PinMode[NumberOfPorts];
            pinConf = new PinConf[NumberOfPorts];

            var configurationLowRegister = new DoubleWordRegister(this, 0x44444444);
            var configurationHighRegister = new DoubleWordRegister(this, 0x44444444);
            for (var offset = 0; offset < 32; offset += 4)
            {
                var lowId = offset / 4;
                var highId = lowId + 8;

                configurationLowRegister.DefineEnumField<PinMode>(offset, 2, name: $"MODE{lowId}",
                    writeCallback: (_, value) => pinMode[lowId] = value,
                    valueProviderCallback: _ => pinMode[lowId]);
                configurationLowRegister.DefineEnumField<PinConf>(offset + 2, 2, name: $"CNF{lowId}",
                    writeCallback: (_, value) => pinConf[lowId] = value,
                    valueProviderCallback: _ => pinConf[lowId]);

                configurationHighRegister.DefineEnumField<PinMode>(offset, 2, name: $"MODE{highId}",
                    writeCallback: (_, value) => pinMode[highId] = value,
                    valueProviderCallback: _ => pinMode[highId]);
                configurationHighRegister.DefineEnumField<PinConf>(offset + 2, 2, name: $"CNF{highId}",
                    writeCallback: (_, value) => pinConf[highId] = value,
                    valueProviderCallback: _ => pinConf[highId]);
            }

            var registersMap = new Dictionary<long, DoubleWordRegister>
            {
                {(long)Registers.ConfigurationLow, configurationLowRegister},
                {(long)Registers.ConfigurationHigh, configurationHighRegister},
                {(long)Registers.InputData, new DoubleWordRegister(this)
                    // upper 16 bits are reserved
                    .WithValueField(0, 16, FieldMode.Read, name: "IDR",
                        valueProviderCallback: _ => BitHelper.GetValueFromBitsArray(State))
                },
                {(long)Registers.OutputData, new DoubleWordRegister(this)
                    // upper 16 bits are reserved
                    .WithValueField(0, 16, name: "ODR",
                        writeCallback: (_, value) => {
                            SetConnectionsStateUsingBits((uint)value);
                        },
                        valueProviderCallback: _ => BitHelper.GetValueFromBitsArray(Connections.Values.Select(x=>x.IsSet)))
                },
                {(long)Registers.BitSetReset, new DoubleWordRegister(this)
                    .WithValueField(16, 16, FieldMode.Write, name: "BR",
                        writeCallback: (_, value) => SetBitsFromMask((uint)value, false))
                    .WithValueField(0, 16, FieldMode.Write, name: "BS",
                        writeCallback: (_, value) => SetBitsFromMask((uint)value, true))
                },
                {(long)Registers.BitReset, new DoubleWordRegister(this)
                    // upper 16 bits are reserved
                    .WithValueField(0, 16, FieldMode.Write, name: "BR",
                        writeCallback: (_, value) => SetBitsFromMask((uint)value, false))
                }
            };

            registers = new DoubleWordRegisterCollection(this, registersMap);

            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers.Write(offset, value);
        }

        public override void OnGPIO(int number, bool value)
        {
            if (!CheckPinNumber(number))
            {
                return;
            }

            if (pinMode[number] != PinMode.Input)
            {
                this.Log(LogLevel.Warning, "Received a signal on the output pin #{0}", number);
                return;
            }

            base.OnGPIO(number, value);
            Connections[number].Set(value);
        }

        public override void Reset()
        {
            base.Reset();
            registers.Reset();
        }

        private void SetBitsFromMask(uint mask, bool state)
        {
            foreach (var bit in BitHelper.GetSetBits(mask))
            {
                if (pinMode[bit] == PinMode.Input)
                {
                    // Commented as this is used for setting pull-up/pull-down state
                    // this.Log(LogLevel.Warning, "Trying to set the state of the input pin #{0}", bit);
                    continue;
                }

                Connections[bit].Set(state);
                State[bit] = state;
            }
        }

        private readonly DoubleWordRegisterCollection registers;
        private readonly PinMode[] pinMode;
        private readonly PinConf[] pinConf;

        private const int NumberOfPorts = 16;

        private enum Registers
        {
            ConfigurationLow = 0x00,
            ConfigurationHigh = 0x04,
            InputData = 0x08,
            OutputData = 0x0C,
            BitSetReset = 0x10,
            BitReset = 0x14,
            PortConfigurationLock = 0x18
        }

        private enum PinMode
        {
            Input = 0,
            Output10Mhz = 1,
            Output2Mhz = 2,
            Output50Mhz = 3
        }

        private enum PinConf
        {
            Analog_OutputPushPull = 0,
            Floating_OutputOpenDrain = 1,
            Input_AlternateFunctionPushPull = 2,
            Reserved_AlternateFunctionOpenDrain = 3
        }
    }
}

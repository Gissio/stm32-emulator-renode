/**
 * ST7565 128x64 monochrome LCD controller with 8-bit parallel GPIO interface
 * and keyboard support.
 *
 * GPIO input lines:
 *   0: RSTB (Reset, active low)
 *   1: A0 (Command/Data select: 0=command, 1=data)
 *   2: E (Write strobe, data latched on falling edge)
 *   16-23: 8-bit parallel data (D0-D7)
 *
 * GPIO output lines:
 *   keyEnter (active low by default)
 *   keyUp (active low by default)
 *   keyDown (active low by default)
 *   keyLeft (active low by default)
 *   keyRight (active low by default)
 */

using System;
using System.Collections.Generic;

using Antmicro.Renode.Backends.Display;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Input;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.Video
{
    public class ST7565 : AutoRepaintingVideo, IKeyboard, ISPIPeripheral, IGPIOReceiver, IGPIOSender
    {
        private int width;
        private int height;
        private byte[] displayRAM;
        private int parallelData;

        private bool isData;
        private bool ePin;

        private bool isDisplayOn;
        private bool allPixelsOn;
        private bool segReverse;
        private bool comReverse;
        private bool inverseDisplay;
        private int startLine;
        private int page;
        private int column;
        private bool expectingContrast;

        public GPIO keyEnter { get; }
        public GPIO keySpace { get; }
        public GPIO keyUp { get; }
        public GPIO keyDown { get; }
        public GPIO keyLeft { get; }
        public GPIO keyRight { get; }

        private bool keyEnterInvert;
        private bool keySpaceInvert;
        private bool keyUpInvert;
        private bool keyDownInvert;
        private bool keyLeftInvert;
        private bool keyRightInvert;

        public ST7565(IMachine machine,
            int width = 128,
            int height = 64,
            bool keyEnterInvert = true,
            bool keySpaceInvert = true,
            bool keyUpInvert = true,
            bool keyDownInvert = true,
            bool keyLeftInvert = true,
            bool keyRightInvert = true) : base(machine)
        {
            this.width = width;
            this.height = height;
            displayRAM = new byte[width * (height / 8)];

            Reconfigure(width, height, PixelFormat.RGBX8888);

            isData = false;
            ePin = false;

            this.keyEnterInvert = keyEnterInvert;
            this.keySpaceInvert = keySpaceInvert;
            this.keyUpInvert = keyUpInvert;
            this.keyDownInvert = keyDownInvert;
            this.keyLeftInvert = keyLeftInvert;
            this.keyRightInvert = keyRightInvert;

            keyEnter = new GPIO();
            keySpace = new GPIO();
            keyUp = new GPIO();
            keyDown = new GPIO();
            keyLeft = new GPIO();
            keyRight = new GPIO();
        }

        public override void Reset()
        {
            Array.Clear(displayRAM, 0, displayRAM.Length);

            isDisplayOn = false;
            allPixelsOn = false;
            segReverse = false;
            comReverse = false;
            inverseDisplay = false;
            startLine = 0;
            page = 0;
            column = 0;
            expectingContrast = false;

            keyEnter.Set(keyEnterInvert);
            keySpace.Set(keySpaceInvert);
            keyUp.Set(keyUpInvert);
            keyDown.Set(keyDownInvert);
            keyLeft.Set(keyLeftInvert);
            keyRight.Set(keyRightInvert);
        }

        // Display rendering
        protected override void Repaint()
        {
            if (!isDisplayOn)
            {
                Array.Clear(buffer, 0, buffer.Length);
                return;
            }

            for (int py = 0; py < height; py++)
            {
                for (int px = 0; px < width; px++)
                {
                    // Don't apply segReverse/comReverse here - these compensate
                    // for physical LCD mounting and the firmware already accounts
                    // for them in how it writes to display RAM.
                    int srcX = px;
                    int srcY = py;

                    int pageIdx = srcY / 8;
                    int bitIdx = srcY % 8;

                    bool pixelOn;
                    if (allPixelsOn)
                    {
                        pixelOn = true;
                    }
                    else
                    {
                        int ramIndex = pageIdx * width + srcX;
                        pixelOn = (displayRAM[ramIndex] & (1 << bitIdx)) != 0;
                    }

                    if (inverseDisplay)
                    {
                        pixelOn = !pixelOn;
                    }

                    int bufferIndex = (py * width + px) * 4;
                    byte color = pixelOn ? (byte)0x00 : (byte)0xFF;
                    buffer[bufferIndex + 0] = color; // R
                    buffer[bufferIndex + 1] = color; // G
                    buffer[bufferIndex + 2] = color; // B
                    buffer[bufferIndex + 3] = 0x00;  // X
                }
            }
        }

        private void ProcessByte(int value)
        {
            if (!isData)
            {
                ProcessCommand((byte)value);
            }
            else
            {
                // Data write to display RAM
                if (page < (height / 8) && column < width)
                {
                    displayRAM[page * width + column] = (byte)value;
                }
                column++;
                if (column >= width)
                {
                    column = 0;
                }
            }
        }

        private void ProcessCommand(byte cmd)
        {
            if (expectingContrast)
            {
                // Second byte of electronic volume command (cosmetic, ignore)
                expectingContrast = false;
                return;
            }

            if (cmd <= 0x0F)
            {
                // Set column address lower nibble
                column = (column & 0xF0) | (cmd & 0x0F);
            }
            else if (cmd >= 0x10 && cmd <= 0x1F)
            {
                // Set column address upper nibble
                column = (column & 0x0F) | ((cmd & 0x0F) << 4);
            }
            else if (cmd >= 0x20 && cmd <= 0x27)
            {
                // Regulation ratio (ignore)
            }
            else if (cmd >= 0x28 && cmd <= 0x2F)
            {
                // Power control (ignore)
            }
            else if (cmd >= 0x40 && cmd <= 0x7F)
            {
                // Set start line
                startLine = cmd & 0x3F;
            }
            else if (cmd == 0x81)
            {
                // Electronic volume (two-byte command)
                expectingContrast = true;
            }
            else if (cmd == 0xA0)
            {
                segReverse = false;
            }
            else if (cmd == 0xA1)
            {
                segReverse = true;
            }
            else if (cmd == 0xA2 || cmd == 0xA3)
            {
                // Bias setting (ignore)
            }
            else if (cmd == 0xA4)
            {
                allPixelsOn = false;
            }
            else if (cmd == 0xA5)
            {
                allPixelsOn = true;
            }
            else if (cmd == 0xA6)
            {
                inverseDisplay = false;
            }
            else if (cmd == 0xA7)
            {
                inverseDisplay = true;
            }
            else if (cmd == 0xAC || cmd == 0xAD)
            {
                // Static indicator (ignore)
            }
            else if (cmd == 0xAE)
            {
                isDisplayOn = false;
            }
            else if (cmd == 0xAF)
            {
                isDisplayOn = true;
            }
            else if (cmd >= 0xB0 && cmd <= 0xB7)
            {
                page = cmd & 0x07;
            }
            else if (cmd == 0xC0)
            {
                comReverse = false;
            }
            else if (cmd == 0xC8)
            {
                comReverse = true;
            }
            else if (cmd == 0xE0)
            {
                // Read-modify-write start (ignore)
            }
            else if (cmd == 0xE2)
            {
                // Software reset
                Array.Clear(displayRAM, 0, displayRAM.Length);
                isDisplayOn = false;
                allPixelsOn = false;
                segReverse = false;
                comReverse = false;
                inverseDisplay = false;
                startLine = 0;
                page = 0;
                column = 0;
            }
            else if (cmd == 0xE3)
            {
                // NOP
            }
            else if (cmd == 0xEE)
            {
                // Read-modify-write end (ignore)
            }
            else if (cmd == 0xF0)
            {
                // Test (ignore)
            }
            else
            {
                this.Log(LogLevel.Info, $"Unknown command: 0x{cmd:X2}");
            }
        }

        // GPIO interface
        public void OnGPIO(int index, bool value)
        {
            switch (index)
            {
                case 0:
                    // RSTB (Reset, active low)
                    if (!value)
                    {
                        Reset();
                    }
                    break;

                case 1:
                    // A0 (Command/Data): 0=command, 1=data
                    isData = value;
                    break;

                case 2:
                    // E (Write strobe) - latch on falling edge
                    if (ePin && !value)
                    {
                        ProcessByte(parallelData & 0xFF);
                    }
                    ePin = value;
                    break;

                default:
                    // Parallel data bits (indices 16-23 -> D0-D7)
                    if (index >= 16 && index <= 23)
                    {
                        int bit = index - 16;
                        int mask = 1 << bit;
                        parallelData = (parallelData & ~mask) | (value ? mask : 0);
                    }
                    break;
            }
        }

        // SPI stub (display is GPIO-only, but ISPIPeripheral needed for @ spi1 attachment)
        public byte Transmit(byte value)
        {
            return 0;
        }

        public void FinishTransmission()
        {
        }

        // Keyboard interface
        public void Press(KeyScanCode scanCode)
        {
            switch (scanCode)
            {
                case KeyScanCode.Enter:
                    keyEnter.Set(!keyEnterInvert);
                    break;

                case KeyScanCode.Space:
                    keySpace.Set(!keySpaceInvert);
                    break;

                case KeyScanCode.Up:
                    keyUp.Set(!keyUpInvert);
                    break;

                case KeyScanCode.Down:
                    keyDown.Set(!keyDownInvert);
                    break;

                case KeyScanCode.Left:
                    keyLeft.Set(!keyLeftInvert);
                    break;

                case KeyScanCode.Right:
                    keyRight.Set(!keyRightInvert);
                    break;
            }
        }

        public void Release(KeyScanCode scanCode)
        {
            switch (scanCode)
            {
                case KeyScanCode.Enter:
                    keyEnter.Set(keyEnterInvert);
                    break;

                case KeyScanCode.Space:
                    keySpace.Set(keySpaceInvert);
                    break;

                case KeyScanCode.Up:
                    keyUp.Set(keyUpInvert);
                    break;

                case KeyScanCode.Down:
                    keyDown.Set(keyDownInvert);
                    break;

                case KeyScanCode.Left:
                    keyLeft.Set(keyLeftInvert);
                    break;

                case KeyScanCode.Right:
                    keyRight.Set(keyRightInvert);
                    break;
            }
        }
    }
}

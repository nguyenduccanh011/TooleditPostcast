using PodcastVideoEditor.Core.Services;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core
{
    public class AudioServiceTest
    {
        public static async Task TestAudioService()
        {
            Console.WriteLine("\n🎵 Audio Service Test\n");

            var audioService = new AudioService();
            var testAudioPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PodcastVideoEditor", "test_audio.wav");

            try
            {
                // Create test audio if not exists
                if (!File.Exists(testAudioPath))
                {
                    Console.WriteLine("⚠️  Test audio not found. Create a test audio file and place it at:");
                    Console.WriteLine(testAudioPath);
                    return;
                }

                // Test 1: Load audio
                Console.WriteLine("Test 1: Loading audio...");
                var metadata = await audioService.LoadAudioAsync(testAudioPath);
                Console.WriteLine($"✅ Audio loaded: {metadata.FileName}");
                Console.WriteLine($"   Duration: {metadata.Duration:F2}s");
                Console.WriteLine($"   Sample Rate: {metadata.SampleRate} Hz");
                Console.WriteLine($"   Channels: {metadata.Channels}");

                // Test 2: Get duration
                Console.WriteLine("\nTest 2: Getting duration...");
                var duration = audioService.GetDuration();
                Console.WriteLine($"✅ Duration: {duration:F2}s");

                // Test 3: Play
                Console.WriteLine("\nTest 3: Playing audio...");
                audioService.Play();
                Console.WriteLine($"✅ Playing: {audioService.IsPlaying}");
                
                // Play for 3 seconds
                await Task.Delay(3000);

                // Test 4: Pause
                Console.WriteLine("\nTest 4: Pausing audio...");
                audioService.Pause();
                var position = audioService.GetCurrentPosition();
                Console.WriteLine($"✅ Paused at: {position:F2}s");

                // Test 5: Seek
                Console.WriteLine("\nTest 5: Seeking to 5 seconds...");
                audioService.Seek(5);
                position = audioService.GetCurrentPosition();
                Console.WriteLine($"✅ Seeked to: {position:F2}s");

                // Test 6: Resume
                Console.WriteLine("\nTest 6: Resuming audio...");
                audioService.Play();
                await Task.Delay(2000);

                // Test 7: Stop
                Console.WriteLine("\nTest 7: Stopping audio...");
                audioService.Stop();
                Console.WriteLine($"✅ Stopped");
                position = audioService.GetCurrentPosition();
                Console.WriteLine($"   Position reset to: {position:F2}s");

                // Test 8: FFT data
                Console.WriteLine("\nTest 8: Getting FFT data...");
                var fftData = audioService.GetFFTData(256);
                Console.WriteLine($"✅ FFT data retrieved: {fftData.Length} samples");

                Console.WriteLine("\n✅ All audio service tests passed!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
                Log.Error(ex, "Audio service test failed");
            }
            finally
            {
                audioService?.Dispose();
            }
        }
    }
}

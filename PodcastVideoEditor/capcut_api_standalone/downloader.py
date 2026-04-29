import os
import subprocess
import time
import requests
import shutil
from requests.exceptions import RequestException, Timeout
from urllib.parse import urlparse, unquote

def download_video(video_url, draft_name, material_name):
    """
    Download video to specified directory
    :param video_url: Video URL
    :param draft_name: Draft name
    :param material_name: Material name
    :return: Local video path
    """
    # Ensure directory exists
    video_dir = f"{draft_name}/assets/video"
    os.makedirs(video_dir, exist_ok=True)
    
    # Generate local filename
    local_path = f"{video_dir}/{material_name}"
    
    # Check if file already exists
    if os.path.exists(local_path):
        print(f"Video file already exists: {local_path}")
        return local_path
    
    try:
        # Use ffmpeg to download video
        command = [
            'ffmpeg',
            '-i', video_url,
            '-c', 'copy',  # Direct copy, no re-encoding
            local_path
        ]
        subprocess.run(command, check=True, capture_output=True)
        return local_path
    except subprocess.CalledProcessError as e:
        raise Exception(f"Failed to download video: {e.stderr.decode('utf-8')}")

def download_image(image_url, draft_name, material_name):
    """
    Download image to specified directory, and convert to PNG format
    :param image_url: Image URL
    :param draft_name: Draft name
    :param material_name: Material name
    :return: Local image path
    """
    # Ensure directory exists
    image_dir = f"{draft_name}/assets/image"
    os.makedirs(image_dir, exist_ok=True)
    
    # Uniformly use png format
    local_path = f"{image_dir}/{material_name}"
    
    # Check if file already exists
    if os.path.exists(local_path):
        print(f"Image file already exists: {local_path}")
        return local_path
    
    try:
        # Use ffmpeg to download and convert image to PNG format
        command = [
            'ffmpeg',
            '-headers', 'User-Agent: Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.114 Safari/537.36\r\nReferer: https://www.163.com/\r\n',
            '-i', image_url,
            '-vf', 'format=rgba',  # Convert to RGBA format to support transparency
            '-frames:v', '1',      # Ensure only one frame is processed
            '-y',                  # Overwrite existing files
            local_path
        ]
        subprocess.run(command, check=True, capture_output=True)
        return local_path
    except subprocess.CalledProcessError as e:
        raise Exception(f"Failed to download image: {e.stderr.decode('utf-8')}")

def download_audio(audio_url, draft_name, material_name):
    """
    Download audio and transcode to MP3 format to specified directory
    :param audio_url: Audio URL
    :param draft_name: Draft name
    :param material_name: Material name
    :return: Local audio path
    """
    # Ensure directory exists
    audio_dir = f"{draft_name}/assets/audio"
    os.makedirs(audio_dir, exist_ok=True)
    
    # Generate local filename (keep .mp3 extension)
    local_path = f"{audio_dir}/{material_name}"
    
    # Check if file already exists
    if os.path.exists(local_path):
        print(f"Audio file already exists: {local_path}")
        return local_path
    
    try:
        # Use ffmpeg to download and transcode to MP3 (key modification: specify MP3 encoder)
        command = [
            'ffmpeg',
            '-i', audio_url,          # Input URL
            '-c:a', 'libmp3lame',     # Force encode audio stream to MP3
            '-q:a', '2',              # Set audio quality (0-9, 0 is best, 2 balances quality and file size)
            '-y',                     # Overwrite existing files (optional)
            local_path                # Output path
        ]
        subprocess.run(command, check=True, capture_output=True, text=True)
        return local_path
    except subprocess.CalledProcessError as e:
        raise Exception(f"Failed to download audio:\n{e.stderr}")

def download_audio_file(url: str, local_filename: str, max_retries=3, timeout=180):
    """Download audio and transcode to MP3 format using ffmpeg.
    
    This ensures CapCut compatibility regardless of the source audio format.
    Falls back to raw download if ffmpeg transcoding fails.
    
    Args:
        url: Remote URL or local file path of the audio source.
        local_filename: Target path (should end with .mp3).
        max_retries: Number of retry attempts.
        timeout: Timeout in seconds for each attempt.
    
    Returns:
        True if download succeeded and file is valid, False otherwise.
    """
    import logging
    logger = logging.getLogger('flask_video_generator')
    
    # Ensure target directory exists
    directory = os.path.dirname(local_filename)
    if directory and not os.path.exists(directory):
        os.makedirs(directory, exist_ok=True)

    # If source is a local file, transcode directly
    if os.path.exists(url) and os.path.isfile(url):
        logger.info(f"Transcoding local audio file: {url} -> {local_filename}")
        try:
            command = [
                'ffmpeg', '-y',
                '-i', url,
                '-c:a', 'libmp3lame',
                '-q:a', '2',
                '-vn',  # Strip any video streams (e.g. album art)
                local_filename
            ]
            subprocess.run(command, check=True, capture_output=True, timeout=timeout)
            if os.path.exists(local_filename) and os.path.getsize(local_filename) > 0:
                logger.info(f"Audio transcoded successfully: {local_filename}")
                return True
            else:
                logger.error(f"Transcoded audio file is empty or missing: {local_filename}")
                return False
        except Exception as e:
            logger.error(f"FFmpeg transcode failed for local file {url}: {e}")
            # Fallback: raw copy
            try:
                shutil.copy2(url, local_filename)
                return os.path.exists(local_filename) and os.path.getsize(local_filename) > 0
            except Exception as e2:
                logger.error(f"Fallback copy also failed: {e2}")
                return False

    # Remote URL: download + transcode via ffmpeg (streaming)
    retries = 0
    while retries < max_retries:
        try:
            if retries > 0:
                wait_time = 2 ** retries
                logger.info(f"Retrying audio download in {wait_time}s... (Attempt {retries+1}/{max_retries})")
                time.sleep(wait_time)

            logger.info(f"Downloading & transcoding audio: {url} -> {local_filename}")
            start_time = time.time()

            # Method 1: Let ffmpeg handle the download + transcode directly
            command = [
                'ffmpeg', '-y',
                '-i', url,
                '-c:a', 'libmp3lame',
                '-q:a', '2',
                '-vn',  # Strip video streams
                local_filename
            ]
            result = subprocess.run(command, capture_output=True, timeout=timeout)
            
            if result.returncode == 0 and os.path.exists(local_filename) and os.path.getsize(local_filename) > 0:
                elapsed = time.time() - start_time
                file_size = os.path.getsize(local_filename)
                logger.info(f"Audio download+transcode completed in {elapsed:.2f}s ({file_size/1024:.1f}KB): {local_filename}")
                return True
            else:
                stderr_msg = result.stderr.decode('utf-8', errors='replace')[-500:] if result.stderr else ''
                logger.warning(f"FFmpeg returned code {result.returncode} for {url}: {stderr_msg}")
                
                # Fallback: raw download (the file might already be MP3)
                logger.info(f"Falling back to raw download for: {url}")
                success = download_file(url, local_filename, max_retries=1, timeout=timeout)
                if success and os.path.exists(local_filename) and os.path.getsize(local_filename) > 0:
                    return True

        except subprocess.TimeoutExpired:
            logger.warning(f"FFmpeg audio download timed out after {timeout}s for {url}")
        except Exception as e:
            logger.error(f"Audio download error for {url}: {e}")

        # Clean up partial file
        if os.path.exists(local_filename):
            try:
                os.remove(local_filename)
            except:
                pass

        retries += 1

    logger.error(f"Audio download failed after {max_retries} attempts: {url}")
    return False


def download_file(url:str, local_filename, max_retries=3, timeout=180):
    # Check whether the source is a local file path.
    if os.path.exists(url) and os.path.isfile(url):
        # Local file: copy directly.
        directory = os.path.dirname(local_filename)
        
        # Create target directory when needed.
        if directory and not os.path.exists(directory):
            os.makedirs(directory, exist_ok=True)
            print(f"Created directory: {directory}")
        
        print(f"Copying local file: {url} to {local_filename}")
        start_time = time.time()
        
        # Copy file
        shutil.copy2(url, local_filename)
        
        print(f"Copy completed in {time.time()-start_time:.2f} seconds")
        print(f"File saved as: {os.path.abspath(local_filename)}")
        return True
    
    # Remote download logic.
    # Extract directory part
    directory = os.path.dirname(local_filename)

    retries = 0
    while retries < max_retries:
        try:
            if retries > 0:
                wait_time = 2 ** retries  # Exponential backoff strategy
                print(f"Retrying in {wait_time} seconds... (Attempt {retries+1}/{max_retries})")
                time.sleep(wait_time)
            
            print(f"Downloading file: {local_filename}")
            start_time = time.time()
            
            # Create directory (if it doesn't exist)
            if directory and not os.path.exists(directory):
                os.makedirs(directory, exist_ok=True)
                print(f"Created directory: {directory}")

            # Add headers
            headers = {
                'User-Agent': 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.114 Safari/537.36',
                'Referer': 'https://www.163.com/',  # Required by some CDN endpoints
                'Accept': 'image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8',
                'Accept-Language': 'zh-CN,zh;q=0.9,en;q=0.8'
            }

            with requests.get(url, stream=True, timeout=timeout, headers=headers) as response:
                response.raise_for_status()
                
                total_size = int(response.headers.get('content-length', 0))
                block_size = 1024
                
                with open(local_filename, 'wb') as file:
                    bytes_written = 0
                    for chunk in response.iter_content(block_size):
                        if chunk:
                            file.write(chunk)
                            bytes_written += len(chunk)
                            
                            if total_size > 0:
                                progress = bytes_written / total_size * 100
                                # For frequently updated progress, consider using logger.debug or more granular control to avoid large log files
                                # Or only output progress to console, not write to file
                                print(f"\r[PROGRESS] {progress:.2f}% ({bytes_written/1024:.2f}KB/{total_size/1024:.2f}KB)", end='')
                                pass # Avoid printing too much progress information in log files
                
                if total_size > 0:
                    # print() # Original newline
                    pass
                print(f"Download completed in {time.time()-start_time:.2f} seconds")
                print(f"File saved as: {os.path.abspath(local_filename)}")
                return True
                
        except Timeout:
            print(f"Download timed out after {timeout} seconds")
        except RequestException as e:
            print(f"Request failed: {e}")
        except Exception as e:
            print(f"Unexpected error during download: {e}")
        
        retries += 1
    
    print(f"Download failed after {max_retries} attempts for URL: {url}")
    return False


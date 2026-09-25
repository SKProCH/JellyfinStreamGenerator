var showStreamGeneratorPopup = function (itemId, serverId) {
    const apiClient = window.ApiClient;
    const getItemId = value => typeof value === 'string' ? value : (typeof (value?.Id || value?.id) === 'string' ? (value.Id || value.id) : null);
    const locationMatch = (window.location.hash + window.location.search).match(/[?&#](?:id|itemId)=([0-9a-f-]{32,36})/i);
    const normalizedItemId = locationMatch ? locationMatch[1] : getItemId(itemId);

    if (!apiClient) {
        console.error('StreamGenerator: Jellyfin ApiClient is not available');
        return;
    }

    const escapeHtml = value => String(value ?? '').replace(/[&<>'"]/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[character]));
    const showToast = message => {
        const toast = document.createElement('div');
        toast.textContent = message;
        Object.assign(toast.style, { position: 'fixed', bottom: '20px', left: '50%', transform: 'translateX(-50%)', backgroundColor: '#333', color: '#fff', padding: '10px 20px', borderRadius: '20px', zIndex: '100000', fontSize: '14px', boxShadow: '0 2px 10px rgba(0,0,0,.5)' });
        document.body.appendChild(toast);
        setTimeout(() => { toast.style.transition = 'opacity .5s'; toast.style.opacity = '0'; setTimeout(() => toast.remove(), 500); }, 2500);
    };
    const formatBitrate = bitrate => bitrate >= 1000000 ? (bitrate / 1000000).toFixed(bitrate % 1000000 ? 2 : 0) + ' Mbps' : Math.round(bitrate / 1000) + ' Kbps';
    const codecName = codec => codec ? codec.toUpperCase() : 'unknown format';
    const isCodec = (stream, codec) => String(stream?.Codec || '').toLowerCase() === codec;

    if (!normalizedItemId) {
        showToast('Cannot get the item ID for this item.');
        return;
    }

    Promise.all([
        apiClient.getItem(apiClient.getCurrentUserId(), normalizedItemId),
        apiClient.getJSON(apiClient.getUrl('StreamGenerator/Settings')).catch(() => ({})),
        apiClient.getJSON(apiClient.getUrl('Users/' + apiClient.getCurrentUserId())).catch(() => ({}))
    ]).then(([item, settings, user]) => {
        if (!item?.MediaSources?.length) {
            showToast('Cannot get media sources for this item.');
            return;
        }

        const mediaSource = item.MediaSources[0];
        const streams = mediaSource.MediaStreams || [];
        const videoStreams = streams.filter(stream => stream.Type === 'Video');
        const audioStreams = streams.filter(stream => stream.Type === 'Audio');
        const subtitleStreams = streams.filter(stream => stream.Type === 'Subtitle');
        const policy = user?.Policy || {};
        const canVideoTranscode = policy.EnableVideoPlaybackTranscoding !== false;
        const canAudioTranscode = policy.EnableAudioPlaybackTranscoding !== false;
        const canRemux = policy.EnablePlaybackRemuxing !== false;
        const defaultVideo = videoStreams[0];
        const defaultAudio = audioStreams.find(stream => stream.Index === mediaSource.DefaultAudioStreamIndex) || audioStreams[0];

        const streamTitle = stream => stream?.DisplayTitle || stream?.Title || ('Track ' + stream?.Index);
        const normalizedTitle = title => String(title || '').normalize('NFC').replace(/\s+/g, '').toLocaleLowerCase();
        const itemTitles = new Set([item.Name, item.OriginalTitle].map(normalizedTitle).filter(Boolean));
        const videoLabel = stream => {
            const title = streamTitle(stream);
            const visibleTitle = itemTitles.has(normalizedTitle(title)) ? '' : title;
            return [visibleTitle, stream.Width && stream.Height ? stream.Width + '×' + stream.Height : null, codecName(stream.Codec), stream.VideoRange].filter(Boolean).join(' · ');
        };
        const audioLabel = stream => [stream.Language && stream.Language !== 'und' ? stream.Language.toUpperCase() : null, streamTitle(stream), codecName(stream.Codec), stream.ChannelLayout || (stream.Channels ? stream.Channels + ' ch' : null)].filter(Boolean).join(' · ');
        const languageMatches = (stream, language) => Boolean(language && stream.Language && stream.Language.toLowerCase() === language);

        const userConfiguration = user.Configuration || {};
        const preferredLanguages = (userConfiguration.SubtitleLanguagePreference || '').split(',').map(language => language.trim().toLowerCase()).filter(Boolean);
        const forcedSubtitles = subtitleStreams.filter(stream => stream.IsForced);
        const findAutoSubtitle = audioIndex => {
            const selectedAudio = audioStreams.find(stream => stream.Index === audioIndex) || defaultAudio;
            const language = selectedAudio?.Language?.toLowerCase();
            return forcedSubtitles.slice().sort((left, right) => {
                const audioScore = Number(languageMatches(right, language)) - Number(languageMatches(left, language));
                if (audioScore) return audioScore;
                const preferredScore = Number(preferredLanguages.some(value => languageMatches(right, value))) - Number(preferredLanguages.some(value => languageMatches(left, value)));
                if (preferredScore) return preferredScore;
                if (left.IsDefault !== right.IsDefault) return right.IsDefault ? 1 : -1;
                return left.Index - right.Index;
            })[0] || null;
        };
        const subtitleLabel = stream => {
            let label = streamTitle(stream);
            if (stream.Language && stream.Language !== 'und' && !label.toLowerCase().includes(stream.Language.toLowerCase())) label = stream.Language.toUpperCase() + ' · ' + label;
            if (stream.IsForced && !/\bforced\b/i.test(label)) label = 'Forced · ' + label;
            return label + (stream.IsExternal ? ' · external' : '');
        };

        let autoSubtitle = findAutoSubtitle(defaultAudio?.Index ?? mediaSource.DefaultAudioStreamIndex);
        let autoSubtitleIndex = autoSubtitle?.Index ?? null;
        const autoSubtitleLabel = () => autoSubtitle ? 'Auto · ' + subtitleLabel(autoSubtitle) : 'Auto · no subtitles';
        const audioOptions = '<option value="">Auto · ' + escapeHtml(audioLabel(defaultAudio)) + '</option>' + audioStreams.map(stream => '<option value="' + stream.Index + '">' + escapeHtml(audioLabel(stream)) + '</option>').join('');
        const videoOptions = videoStreams.map(stream => '<option value="' + stream.Index + '">' + escapeHtml(videoLabel(stream)) + '</option>').join('');
        const subtitleOptions = '<option value="auto" selected>' + escapeHtml(autoSubtitleLabel()) + '</option><option value="-1">Off</option>' + subtitleStreams.map(stream => '<option value="' + stream.Index + '">' + escapeHtml(subtitleLabel(stream)) + '</option>').join('');
        const defaultSubtitleMethod = autoSubtitle ? (canVideoTranscode ? 'Encode' : 'Hls') : 'Drop';

        const overlay = document.createElement('div');
        Object.assign(overlay.style, { position: 'fixed', inset: '0', backgroundColor: 'rgba(0,0,0,.7)', zIndex: '99999', display: 'flex', alignItems: 'center', justifyContent: 'center', fontFamily: 'sans-serif' });
        const modal = document.createElement('div');
        modal.className = 'sg-modal';
        Object.assign(modal.style, { backgroundColor: '#1e1e1e', color: '#fff', padding: '20px', borderRadius: '8px', boxSizing: 'border-box', width: 'min(1120px, calc(100vw - 32px))', maxWidth: 'none', maxHeight: '92vh', overflowY: 'auto', boxShadow: '0 4px 20px rgba(0,0,0,.5)' });
        const selectStyle = 'width:100%;padding:8px;margin-top:5px;margin-bottom:8px;background:#333;color:#fff;border:1px solid #444;border-radius:4px;box-sizing:border-box;';
        const presetHours = [1, 6, 12, 24, 72, 168, 360, 720];
        const presetLabels = { 1: '1 Hour', 6: '6 Hours', 12: '12 Hours', 24: '1 Day', 72: '3 Days', 168: '7 Days', 360: '15 Days', 720: '30 Days' };
        const maxDuration = settings.MaxTokenDurationHours;
        const defaultDuration = settings.DefaultTokenDurationHours;
        const validDurations = presetHours.filter(hours => maxDuration == null || hours <= maxDuration);
        if (maxDuration != null && !validDurations.includes(maxDuration)) { validDurations.push(maxDuration); presetLabels[maxDuration] = maxDuration + ' Hours (Max)'; }
        if (defaultDuration != null && !validDurations.includes(defaultDuration) && (maxDuration == null || defaultDuration <= maxDuration)) { validDurations.push(defaultDuration); presetLabels[defaultDuration] = defaultDuration + ' Hours (Default)'; }
        validDurations.sort((left, right) => left - right);
        const durationOptions = validDurations.map(hours => '<option value="' + hours + '"' + (hours === defaultDuration ? ' selected' : '') + '>' + (presetLabels[hours] || hours + ' Hours') + '</option>').join('') + (maxDuration == null ? '<option value=""' + (defaultDuration == null ? ' selected' : '') + '>Infinite</option>' : '');

        modal.innerHTML = '<style>' +
            '#streamGeneratorForm{display:block!important;width:100%!important;max-width:none!important;box-sizing:border-box}.sg-grid{display:grid;width:100%;box-sizing:border-box;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px;margin:14px 0 18px}.sg-card{background:#292929;border:1px solid #444;border-radius:8px;padding:14px;min-width:0;box-sizing:border-box}.sg-card h3{margin:0 0 12px;font-size:1em}.sg-label{display:block;margin-top:12px;font-size:.9em;color:#ddd}.sg-meta{color:#aaa;font-size:.84em;line-height:1.45;min-height:2.5em;margin-top:6px}.sg-result{color:#8fd18a;font-size:.85em;line-height:1.4;margin-top:7px}.sg-warning{color:#f0bd72;font-size:.84em;line-height:1.4;margin-top:8px}.sg-actions{display:flex;justify-content:flex-end;margin-top:18px}@media(max-width:800px){.sg-grid{grid-template-columns:minmax(0,1fr)}.sg-card{padding:12px}}@media(max-width:600px){.sg-modal{width:calc(100vw - 20px)!important;max-height:calc(100vh - 20px)!important;padding:14px!important;border-radius:6px!important}.sg-header{align-items:stretch!important;flex-direction:column}.sg-header button{align-self:flex-start}.sg-actions{gap:8px;flex-wrap:wrap}.sg-actions button{margin-left:0!important}.sg-card select{font-size:16px!important}}</style>' +
            '<div class="sg-header" style="display:flex;justify-content:space-between;align-items:center;gap:12px;margin-bottom:8px"><div><h2 style="margin:0;font-weight:normal">Generate Stream URL</h2><div style="color:#aaa;font-size:.9em;margin-top:4px">Choose tracks and compatibility independently</div></div><button type="button" id="btnMaxCompatibility" style="padding:7px 10px;border:1px solid #aa5500;border-radius:4px;cursor:pointer;background:transparent;color:#f0bd72">Maximize compatibility</button></div>' +
            '<form id="streamGeneratorForm"><div class="sg-grid">' +
            '<section class="sg-card"><h3>Video</h3><label class="sg-label">Video track<select id="videoStreamIndex" style="' + selectStyle + '">' + videoOptions + '</select></label><div id="videoMeta" class="sg-meta"></div><label class="sg-label"><input type="checkbox" id="videoCompatibility"' + (canVideoTranscode ? '' : ' disabled') + ' style="margin-right:7px">Maximum compatibility</label><div id="videoCompatibilityInfo" class="sg-result"></div>' + (canVideoTranscode ? '<label class="sg-label">Video bitrate <span id="bitrateDisplay"></span><input type="range" id="maxVideoBitrate" style="' + selectStyle + 'cursor:pointer;margin-bottom:0" min="1000000" step="100000" value="1000000"></label>' : '<div class="sg-warning">Video bitrate conversion is unavailable for this user.</div>') + (!canVideoTranscode ? '<div class="sg-warning">Video transcoding is unavailable for this user.</div>' : '') + '</section>' +
            '<section class="sg-card"><h3>Audio</h3><label class="sg-label">Audio track<select id="audioStreamIndex" style="' + selectStyle + '">' + audioOptions + '</select></label><div id="audioMeta" class="sg-meta"></div><label class="sg-label"><input type="checkbox" id="audioCompatibility"' + (canAudioTranscode ? '' : ' disabled') + ' style="margin-right:7px">Maximum compatibility</label><div id="audioCompatibilityInfo" class="sg-result"></div>' + (!canAudioTranscode ? '<div class="sg-warning">Audio transcoding is unavailable for this user.</div>' : '') + '</section>' +
            '<section class="sg-card"><h3>Subtitles</h3><label class="sg-label">Subtitle track<select id="subtitleStreamIndex" style="' + selectStyle + '">' + subtitleOptions + '</select></label><label class="sg-label">Display method<select id="subtitleMethod" style="' + selectStyle + '"><option value="Hls">Separate HLS track</option><option value="Encode"' + (defaultSubtitleMethod === 'Encode' ? ' selected' : '') + (canVideoTranscode ? '' : ' disabled') + '>Burn into video</option><option value="Embed">Embed</option><option value="Drop"' + (defaultSubtitleMethod === 'Drop' ? ' selected' : '') + '>Drop</option></select></label><div id="subtitleInfo" class="sg-result"></div>' + (!canVideoTranscode ? '<div class="sg-warning">Burning subtitles requires video transcoding.</div>' : '') + '</section></div>' +
            '<div id="compatibilityError" class="sg-warning" style="display:none;margin-bottom:12px"></div>' +
            '<details open style="margin-bottom:15px;background:#333;padding:10px;border-radius:4px;border:1px solid #444"><summary style="cursor:pointer;font-weight:bold">Advanced</summary>' +
            '<label style="display:block;margin-top:10px">Token lifetime<select id="tokenDurationHours" style="' + selectStyle + 'margin-bottom:0">' + durationOptions + '</select></label>' +
            '<label style="display:block;margin-top:15px">Segment container<select id="segmentContainer" style="' + selectStyle + 'margin-bottom:0"><option value="auto" selected>Auto</option><option value="mp4">fMP4</option><option value="ts">MPEG-TS</option></select></label><div id="segmentContainerHint" class="sg-meta" style="min-height:0;margin-top:4px"></div>' +
            '<label style="display:flex;align-items:center;margin-top:15px;cursor:pointer"><input type="checkbox" id="copyTimestamps" checked style="margin-right:8px"><span>Copy timestamps</span></label>' +
            (settings.GenerateCustomApiTokens ? '<label style="display:flex;align-items:center;margin-top:15px;cursor:pointer"><input type="checkbox" id="rememberPlaybackProgress"' + (settings.RememberPlaybackProgressByDefault !== false ? ' checked' : '') + ' style="margin-right:8px"><span>Remember playback progress</span></label>' : '') +
            '</details><div class="sg-actions"><button type="button" id="btnCancel" style="padding:8px 16px;border:0;border-radius:4px;cursor:pointer;font-weight:bold;margin-left:10px;background:#444;color:#fff">Close</button><button type="submit" style="padding:8px 16px;border:0;border-radius:4px;cursor:pointer;font-weight:bold;margin-left:10px;background:#52b54b;color:#fff">Generate and Copy</button></div></form>';
        overlay.appendChild(modal);
        document.body.appendChild(overlay);

        const closePopup = () => overlay.remove();
        const videoSelect = modal.querySelector('#videoStreamIndex');
        const audioSelect = modal.querySelector('#audioStreamIndex');
        const subtitleSelect = modal.querySelector('#subtitleStreamIndex');
        const subtitleMethod = modal.querySelector('#subtitleMethod');
        const videoCompatibility = modal.querySelector('#videoCompatibility');
        const audioCompatibility = modal.querySelector('#audioCompatibility');
        const segmentContainerSelect = modal.querySelector('#segmentContainer');
        const bitrateSlider = modal.querySelector('#maxVideoBitrate');
        const bitrateDisplay = modal.querySelector('#bitrateDisplay');
        const selectedVideo = () => videoStreams.find(stream => stream.Index === Number(videoSelect.value)) || defaultVideo;
        const selectedAudio = () => audioSelect.value === '' ? defaultAudio : audioStreams.find(stream => stream.Index === Number(audioSelect.value)) || defaultAudio;
        const getAutomaticContainer = () => {
            const video = selectedVideo();
            const audio = selectedAudio();
            const videoCodec = videoCompatibility.checked && !isCodec(video, 'h264') ? 'h264' : String(video?.Codec || '').toLowerCase();
            const audioCodec = audioCompatibility.checked && !isCodec(audio, 'aac') ? 'aac' : String(audio?.Codec || '').toLowerCase();
            // Prefer fMP4. FFmpeg's MP4 muxer rejects VP8; VP9/AV1 and FLAC/Opus/TrueHD require MP4 mode.
            const fmp4UnsupportedVideoCodecs = ['vp8'];
            return fmp4UnsupportedVideoCodecs.includes(videoCodec) ? 'ts' : 'mp4';
        };
        const updateSegmentContainer = () => {
            const automaticContainer = getAutomaticContainer();
            const containerHint = modal.querySelector('#segmentContainerHint');
            if (segmentContainerSelect.value === 'auto') {
                containerHint.textContent = automaticContainer === 'ts'
                    ? 'VP8 cannot be muxed in fMP4. Auto falls back to MPEG-TS, but some players may not recognize VP8 in TS.'
                    : 'Auto uses fMP4 unless a selected codec cannot be muxed in it.';
            } else {
                containerHint.textContent = segmentContainerSelect.value === 'mp4' && String(selectedVideo()?.Codec || '').toLowerCase() === 'vp8' && !(videoCompatibility.checked && canVideoTranscode)
                    ? 'FFmpeg cannot mux VP8 in fMP4. Enable video compatibility to convert it to H.264, if permitted.'
                    : 'Manual selection: ' + (segmentContainerSelect.value === 'ts' ? 'MPEG-TS' : 'fMP4') + '.';
            }
        };
        const updateBitrate = () => {
            if (!bitrateSlider) return;
            const stream = selectedVideo();
            const sourceBitrate = Number.isFinite(stream?.BitRate) && stream.BitRate > 0 ? stream.BitRate : null;
            const step = Math.max(100000, Math.ceil((sourceBitrate || 140000000) / 100 / 100000) * 100000);
            const max = Math.max(1000000, Math.ceil((sourceBitrate || 140000000) / step) * step);
            bitrateSlider.max = max + step;
            bitrateSlider.step = step;
            bitrateSlider.value = max + step;
            bitrateDisplay.textContent = sourceBitrate ? '· ' + formatBitrate(sourceBitrate) + ' (source)' : '· source bitrate unknown';
        };
        const updateState = () => {
            const video = selectedVideo();
            const audio = selectedAudio();
            const videoNeedsConversion = videoCompatibility.checked && !isCodec(video, 'h264');
            const audioNeedsConversion = audioCompatibility.checked && !isCodec(audio, 'aac');
            modal.querySelector('#videoMeta').textContent = video ? [formatBitrate(video.BitRate || 0), video.Profile, video.RefFrames ? video.RefFrames + ' ref frames' : null].filter(Boolean).join(' · ') : '';
            modal.querySelector('#audioMeta').textContent = audio ? [audio.Codec ? codecName(audio.Codec) : null, audio.ChannelLayout, audio.BitRate ? formatBitrate(audio.BitRate) : null].filter(Boolean).join(' · ') : '';
            modal.querySelector('#videoCompatibilityInfo').textContent = videoNeedsConversion ? codecName(video.Codec) + ' → H.264 · video transcoding' : isCodec(video, 'vp8') ? 'VP8 · not supported by the fMP4 muxer' : !canVideoTranscode && !isCodec(video, 'h264') ? codecName(video?.Codec) + ' · transcoding unavailable' : codecName(video?.Codec) + ' · can be copied';
            modal.querySelector('#audioCompatibilityInfo').textContent = audioNeedsConversion ? codecName(audio.Codec) + ' → AAC · audio transcoding' : !canAudioTranscode && !isCodec(audio, 'aac') ? codecName(audio?.Codec) + ' · transcoding unavailable' : codecName(audio?.Codec) + ' · can be copied';
            const subtitleNeedsConversion = subtitleSelect.value !== '-1' && subtitleMethod.value === 'Encode';
            modal.querySelector('#subtitleInfo').textContent = subtitleSelect.value === '-1' ? 'Subtitles disabled' : subtitleNeedsConversion ? 'Burning subtitles requires video transcoding' : 'Subtitles remain a separate stream';
            updateSegmentContainer();
        };

        modal.querySelector('#btnCancel').addEventListener('click', closePopup);
        modal.querySelector('#btnMaxCompatibility').addEventListener('click', () => { if (canVideoTranscode) videoCompatibility.checked = true; if (canAudioTranscode) audioCompatibility.checked = true; updateState(); });
        videoSelect.addEventListener('change', () => { updateBitrate(); updateState(); });
        audioSelect.addEventListener('change', () => { autoSubtitle = findAutoSubtitle(selectedAudio()?.Index); autoSubtitleIndex = autoSubtitle?.Index ?? null; subtitleSelect.querySelector('option[value="auto"]').textContent = autoSubtitleLabel(); if (subtitleSelect.value === 'auto') subtitleMethod.value = autoSubtitle ? (canVideoTranscode ? 'Encode' : 'Hls') : 'Drop'; updateState(); });
        videoCompatibility.addEventListener('change', updateState);
        audioCompatibility.addEventListener('change', updateState);
        segmentContainerSelect.addEventListener('change', updateSegmentContainer);
        subtitleMethod.addEventListener('change', updateState);
        subtitleSelect.addEventListener('change', event => { if (event.target.value === 'auto') subtitleMethod.value = autoSubtitleIndex == null ? 'Drop' : (canVideoTranscode ? 'Encode' : 'Hls'); if (event.target.value === '-1') subtitleMethod.value = 'Drop'; updateState(); });
        bitrateSlider?.addEventListener('input', event => { const value = Number(event.target.value); const source = selectedVideo()?.BitRate; bitrateDisplay.textContent = value === Number(bitrateSlider.max) ? (source ? '· ' + formatBitrate(source) + ' (source)' : '· source bitrate unknown') : '· ' + formatBitrate(value); });
        overlay.addEventListener('click', event => { if (event.target === overlay) closePopup(); });
        document.addEventListener('keydown', function escape(event) { if (event.key === 'Escape') { document.removeEventListener('keydown', escape); closePopup(); } });
        updateBitrate();
        updateState();

        modal.querySelector('#streamGeneratorForm').addEventListener('submit', event => {
            event.preventDefault();
            const video = selectedVideo();
            const audio = selectedAudio();
            const videoNeedsConversion = videoCompatibility.checked && !isCodec(video, 'h264');
            const audioNeedsConversion = audioCompatibility.checked && !isCodec(audio, 'aac');
            const subtitleNeedsConversion = subtitleSelect.value !== '-1' && subtitleMethod.value === 'Encode';
            const errors = [];
            if ((videoNeedsConversion || subtitleNeedsConversion) && !canVideoTranscode) errors.push('Video transcoding is not allowed for this user.');
            if (audioNeedsConversion && !canAudioTranscode) errors.push('Audio transcoding is not allowed for this user.');
            if (!videoNeedsConversion && !audioNeedsConversion && !subtitleNeedsConversion && !canRemux) errors.push('Playback remuxing is not allowed for this user.');
            if (errors.length) { const error = modal.querySelector('#compatibilityError'); error.textContent = errors.join(' '); error.style.display = 'block'; return; }
            modal.querySelector('button[type="submit"]').disabled = true;
            const selectedBitrate = Number(bitrateSlider?.value);
            const maxVideoBitrate = canVideoTranscode && selectedBitrate !== Number(bitrateSlider?.max) ? selectedBitrate : null;
            const subtitleSelection = subtitleSelect.value;
            const effectiveSubtitleIndex = subtitleSelection === 'auto' ? autoSubtitleIndex : subtitleSelection === '-1' ? -1 : subtitleSelection;
            const effectiveSubtitleMethod = subtitleSelection === 'auto' ? (autoSubtitleIndex == null ? 'Drop' : subtitleMethod.value) : subtitleSelection === '-1' ? 'Drop' : subtitleMethod.value;
            const selectedContainer = segmentContainerSelect.value === 'auto' ? getAutomaticContainer() : segmentContainerSelect.value;
            const queryParams = new URLSearchParams({ deviceId: 'stream_generator', playSessionId: 'stream_generator_random', api_key: '', mediaSourceId: mediaSource.Id, static: false, enableAutoStreamCopy: true, allowVideoStreamCopy: true, allowAudioStreamCopy: true, copyTimestamps: modal.querySelector('#copyTimestamps').checked, segmentContainer: selectedContainer, videoStreamIndex: video.Index });
            if (maxVideoBitrate !== null) queryParams.set('videoBitrate', maxVideoBitrate);
            if (videoNeedsConversion) queryParams.set('videoCodec', 'h264');
            if (audioNeedsConversion) queryParams.set('audioCodec', 'aac');
            if (audioSelect.value !== '') queryParams.set('audioStreamIndex', audioSelect.value);
            if (effectiveSubtitleIndex !== null) { queryParams.set('subtitleStreamIndex', effectiveSubtitleIndex); queryParams.set('subtitleMethod', effectiveSubtitleMethod); }
            const buildUrl = apiKey => { queryParams.set('api_key', apiKey); const url = apiClient.serverAddress() + '/Videos/' + normalizedItemId + '/master.m3u8?' + decodeURIComponent(queryParams.toString()); if (navigator.clipboard && window.isSecureContext) return navigator.clipboard.writeText(url).then(() => showToast('URL copied to clipboard')); const area = document.createElement('textarea'); area.value = url; area.style.position = 'fixed'; area.style.left = '-999999px'; document.body.appendChild(area); area.select(); try { document.execCommand('copy'); showToast('URL copied to clipboard'); } catch (error) { showToast('Failed to copy'); } area.remove(); return Promise.resolve(); };
            const finish = () => {
                if (!settings.GenerateCustomApiTokens) return buildUrl(apiClient.accessToken());
                const tokenQuery = { itemId: normalizedItemId, rememberPlaybackProgress: modal.querySelector('#rememberPlaybackProgress')?.checked ?? settings.RememberPlaybackProgressByDefault !== false };
                const duration = modal.querySelector('#tokenDurationHours').value;
                if (duration !== '') tokenQuery.durationHours = duration;
                return apiClient.fetch({ type: 'POST', url: apiClient.getUrl('StreamGenerator/GenerateToken', tokenQuery), dataType: 'text' })
                    .then(token => buildUrl(token.replace(/^"|"$/g, '')))
                    .catch(() => buildUrl(apiClient.accessToken()));
            };
            finish().catch(() => showToast('Failed to generate stream URL.')).finally(() => { modal.querySelector('button[type="submit"]').disabled = false; });
        });
    }).catch(error => { console.error('StreamGenerator: Failed to load popup data', error); showToast('Failed to load stream options.'); });
};
window.showStreamGeneratorPopup = showStreamGeneratorPopup;

package com.krishna.remindly

import org.junit.Assert.*
import org.junit.Test

class ReleaseChannelTest {
    private fun feed(name: String) = VersionFeed(212000030, name, "Remindly_$name.apk",
        "https://github.com/krishnabhunia/remindly-app/releases/download/v$name/Remindly_$name.apk", "a".repeat(64), 100, "")

    @Test fun betaCannotBeOfferedByStableChannel_evenIfNewerAndPreviouslyCached() {
        val beta = feed("2.12.0-beta.7.30")
        assertTrue(updateAvailable(beta, 2011000))
        assertFalse(updateFeedAllowed(beta, beta = false))
        assertTrue(updateFeedAllowed(beta, beta = true))
        assertTrue(updateFeedAllowed(feed("2.12.0"), beta = false))
        assertFalse(updateFeedAllowed(null, beta = true))
    }

    @Test fun optionalChannelUsesSeparateFeed_andDefaultsToStable() {
        assertFalse(AppSettings().updateBeta)
        assertEquals(UPDATE_FEED_URL, updateFeedUrl(false))
        assertEquals(UPDATE_BETA_FEED_URL, updateFeedUrl(true))
        assertNotEquals(updateFeedUrl(true), updateFeedUrl(false))
    }
}
